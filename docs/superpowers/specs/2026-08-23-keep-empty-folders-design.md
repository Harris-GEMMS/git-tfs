# `--keep-empty-folders`: preserving TFVC empty directories through clone/fetch

## Problem

TFVC allows empty directories; git does not. `git-tfs clone`/`fetch` silently
drops every TFVC folder that contains no files, at two independent points in
the pipeline:

- `TfsChangeset.GetTree()` (`src/GitTfs/Core/TfsChangeset.cs:80`) filters the
  full-tree listing down to `TfsItemType.File` only, used by `CopyTree` (the
  single-snapshot path, see below).
- `ChangeSieve.GetChangesToApply()` (`src/GitTfs/Util/ChangeSieve.cs:91`)
  skips any changeset entry that isn't `TfsItemType.File`, used by
  `TfsChangeset.Apply()` (the per-changeset replay path, see below).

This document specs a `--keep-empty-folders` option that adds a `.gitkeep`
placeholder file to folders that are genuinely empty in TFVC, so they survive
into git.

## Goals / non-goals

**Goal:** when `--keep-empty-folders` is passed to a `clone`/`fetch`/`branch`
invocation, the resulting git tree at the end of that invocation contains a
`.gitkeep` in every TFVC folder that has zero real items beneath it, and no
`.gitkeep` anywhere else.

**Non-goals (explicitly out of scope for this design):**

- **Persistence.** The option is a plain per-invocation flag. It is *not*
  written to remote git config. If a later `fetch` omits the flag, that
  invocation's fetch behaves as if the feature doesn't exist; folders that
  empty out during that fetch will not get a `.gitkeep`, and existing
  `.gitkeep`s are left untouched.
- **Historical fidelity.** Only the *final* tree produced by a given
  `clone`/`fetch` invocation is guaranteed correct. Intermediate commits
  created while replaying a changeset history are not individually
  guaranteed to have correct empty-folder state.
- **Retroactive backfill.** Turning the flag on for a `fetch` that finds
  nothing new to fetch (already up to date) is a no-op — see "Safety
  boundary" below. It never rewrites a commit from a previous invocation.
- **The `checkin`/`rcheckin` direction (git → TFS).** `DirectoryTidier`
  (`src/GitTfs/Core/DirectoryTidier.cs`), which deletes TFS folders that lose
  all their files on checkin, is unchanged. A `.gitkeep`-holding folder is
  not specially recognized on checkin; this is a documented limitation, not
  a solved problem (see "Known limitations").

## Background: two tree-building paths

`GitTfsRemote.FetchWithMerge` (`GitTfsRemote.cs:319`) is the loop behind both
`clone` and `fetch`. For every regular changeset it calls
`GitTfsRemote.Apply()` → `TfsChangeset.Apply()` (`GitTfsRemote.cs:362,787`),
which takes the git tree of the parent commit plus one changeset's delta and
produces the next commit's tree incrementally. **This is the path used for
a plain `clone` from the beginning of history, and for every regular
`fetch`** — not a rare case.

The single-snapshot path, `TfsChangeset.CopyTree()` (`TfsChangeset.cs:104`),
only runs for `QuickFetch` (`GitTfsRemote.cs:686-710`) — i.e.
`clone -c/--changeset/--from <id>` — which takes one full snapshot at a
changeset instead of replaying every changeset up to it.

Both paths ultimately produce exactly one new "tip" commit per invocation
(the last-replayed changeset's commit, or the single snapshot commit). This
is the key fact the design leans on: reconciliation only ever needs to look
at *that one final commit*, once, regardless of which path produced it or
how many changesets were replayed to get there.

## Design

### 1. CLI surface

New option `--keep-empty-folders` on `RemoteOptions`
(`src/GitTfs/Commands/RemoteOptions.cs`), alongside `--ignore-regex`/
`--gitignore`. Available on any command that merges `RemoteOptions.OptionSet`
(`clone`, `fetch`, `init`, ...). Exposed as `RemoteOptions.KeepEmptyFolders`
(bool), read directly by the fetch pipeline for that invocation only — no
config persistence.

Placeholder filename is fixed as `.gitkeep`, not configurable.

**`branch --init`/`--branches=all` is a separate CLI surface, not covered by
the above.** `InitBranch` (`src/GitTfs/Commands/InitBranch.cs`) does not
merge `RemoteOptions.OptionSet` — it hand-rolls its own options (mirroring
`--ignore-regex`/`--except-regex` as its own properties, not delegated to a
shared `RemoteOptions`), and it is invoked as a direct method call from
`Clone.Run()` when `--branches=all` is used, not by re-parsing the command
line. So `--keep-empty-folders` needs its own entry on `InitBranch.OptionSet`
too, for `branch --init` used standalone — see "Propagating to
`--branches=all`" below for why this is also required for the flag to reach
branches auto-discovered during a `clone --branches=all` run.

### 2. Core algorithm: `EmptyFolderTracker`

A new class, `src/GitTfs/Core/EmptyFolderTracker.cs`, the conceptual mirror
of `DirectoryTidier` (which solves the reverse problem for checkin).

**`GetGitKeepPaths(IEnumerable<TfsTreeEntry> allItems)`** — the only method
needed, given the pivot below. Takes a flat listing of every TFVC item
(files and folders, **unfiltered by ignore rules — see next point**) and
walks the folder hierarchy to find every "leaf" empty folder: a folder with
zero files and zero subfolders anywhere beneath it. A folder that contains
only other empty folders does *not* get its own `.gitkeep` — the deepest
empty folder's own `.gitkeep` already implies the intermediate directories
exist in the git tree. Returns the git paths that need a `.gitkeep`.

**Emptiness is judged against the raw TFVC listing, never the
ignore/`.gitignore`-filtered one.** A folder whose only content is excluded
by `--ignore-regex`/`.gitignore` is *not* empty — it had real, intentional
TFVC content that the user chose not to bring into git, which is a different
statement from "this folder was empty in TFVC." Example that motivated this
rule: a `packages/` folder with checked-in NuGet packages, excluded via
`.gitignore` during a migration — it gets no `.gitkeep` (nothing at all in
the resulting git tree for that folder), while a genuinely-empty `docs/`
folder does get one. Getting this backwards would mean any ignored,
non-empty folder grows a spurious placeholder the moment its real content is
filtered out — exactly the opposite of user intent.

`GetGitKeepPaths` only computes the "should have a `.gitkeep`" set — a
one-shot, in-memory, bottom-up walk over the raw listing, no TFS server calls
beyond the one listing query described next. A second helper,
`EmptyFolderTracker.IsGitKeepPath(string gitPath)`, recognizes `.gitkeep` at
the root or at any depth. The caller (`GitTfsRemote`) derives both the add
set and the remove set as a plain set difference: it reads the commit's
current tree, filters its paths through `IsGitKeepPath` to get "currently
has", and diffs that against `GetGitKeepPaths`' "should have" — paths in
"should have" but not "currently has" are adds; paths in "currently has" but
not "should have" are removes. No separate removal algorithm is needed.

### 3. Where reconciliation runs — the pivot

An earlier iteration of this design tried to track folder emptiness
incrementally inside `ChangeSieve`/`TfsChangeset.Apply()`, per changeset,
including live per-directory TFS queries to resolve the raw-vs-ignored
ambiguity. That was dropped for two reasons: it reintroduces a TFS round trip
per touched directory per changeset — and since a plain `clone` replays
every changeset from the start of history through `Apply()` (not a single
snapshot), that cost scales with total project history, not with one
invocation. And it's solving a problem nobody needs solved: only the *final*
state of a `clone`/`fetch` invocation needs to be correct, not every
intermediate replayed commit.

**Revised approach:** `ChangeSieve`/`TfsChangeset.Apply()` are completely
unchanged — zero added risk, zero added TFS calls in the per-changeset replay
loop. Instead, reconciliation runs **exactly once, at the end of the whole
`clone`/`fetch` invocation**, against whichever single commit ended up as the
new tip:

1. After the fetch loop reaches a *complete* stopping point,
   `MaxCommitHash`/`MaxChangesetId` (`GitTfsRemote.cs:149-164`) point at the
   real last-fetched changeset's commit. "Complete" means either the loop
   ran out of changesets normally, or a bounded fetch (`-c`/`--to`) hit its
   requested cutoff — both leave a legitimate, final commit behind. It does
   *not* include the merge-changeset-failure or rename-boundary-pause early
   returns inside `FetchWithMerge`'s loop, which are transient/incomplete
   states; those are simply picked up by whichever later invocation actually
   reaches a complete stopping point.
2. Issue one raw listing query at that exact changeset, files and folders,
   unfiltered — the same root-path / subtree-union logic
   `TfsChangeset.GetFullTree()` already uses for `Summary.Remote.TfsSubtreePaths`,
   so multi-subtree remotes are handled without extra design. When this
   invocation happened to go through `CopyTree`/`QuickFetch`, that path has
   already fetched an equivalent full listing via `GetFullTree()` — issuing
   it again for reconciliation is a redundant TFS round trip for that case
   specifically. Accepted as a minor, bounded cost (once per `QuickFetch`
   invocation, not per changeset) rather than restructuring `CopyTree` to
   thread the listing through; not worth the added coupling.
3. Run `EmptyFolderTracker.GetGitKeepPaths(...)` against it, diff against the
   `.gitkeep` paths already present in the commit's current tree (see set
   difference above), and collect the add/remove set.
4. If empty, done — no-op, no new commit object.
5. If not empty, build a *replacement* commit for that same tip: identical
   parents, author, committer, and message, but a tree with the
   `.gitkeep` adds/removes folded in. Call `UpdateTfsHead(newSha,
   MaxChangesetId)` to point the ref at the replacement.

Cost is now flat: one full listing query per `clone`/`fetch` invocation,
independent of how many changesets were replayed to produce that invocation's
final commit. The *mechanics* run per-remote — each `GitTfsRemote` (each
branch, in a `--branches=all` clone) tracks its own `MaxCommitHash`/
`RemoteRef` independently, so reconciliation itself needs no cross-remote
coordination. Getting the *option* to every remote in a `--branches=all`
clone is a separate concern — see "Propagating to `--branches=all`" below.

### 4. Propagating to `--branches=all`

`RemoteOptions` is a `[StructureMapSingleton]`, so `--keep-empty-folders`
parsed on the `clone` command line is visible to `Clone`, `Fetch`, and `Init`
automatically. It is **not** visible to remotes created via
`--branches=all`, and this is not a mechanics problem solvable by the
per-remote independence described above — it's a wiring gap:

`Clone.Run()` (`src/GitTfs/Commands/Clone.cs`), when `BranchStrategy.All`,
calls `_initBranch.Run()` as a direct C# method call — no command-line
re-parsing happens, so `InitBranch`'s own `OptionSet` never gets a chance to
parse anything for this invocation. And `InitBranch.InitFromDefaultRemote()`
unconditionally does `_remoteOptions = new RemoteOptions();`, a fresh,
disconnected instance — not the injected singleton. For options like
`--ignore-regex`, this is masked by a fallback: `InitFromDefaultRemote()`
recovers the value from the trunk remote's *persisted* config
(`defaultRemote.IgnoreRegexExpression`). `--keep-empty-folders` was
deliberately never persisted (see "Non-goals"), so there is nothing
equivalent to fall back to — without explicit propagation, every branch
discovered by `--branches=all` silently gets `KeepEmptyFolders = false`
regardless of the CLI flag, while the trunk remote (fetched directly by
`Clone`/`Fetch` before `InitBranch` runs) gets it correctly. No error, no
warning — just quietly wrong output for every branch but the trunk.

The fix: `Clone` takes `RemoteOptions` as a constructor dependency (the same
injected singleton `Fetch`/`Init` already read from), and `Clone.Run()`
sets `_initBranch.KeepEmptyFolders = _remoteOptions.KeepEmptyFolders;`
immediately before calling `_initBranch.Run()`. `InitBranch` gets its own
`KeepEmptyFolders` property and `--keep-empty-folders` `OptionSet` entry
(so `branch --init` also supports the flag standalone, consistent with
`--ignore-regex`'s treatment there), and `InitFromDefaultRemote()` applies
it unconditionally onto the fresh `RemoteOptions` it builds per branch — no
if/else fallback needed, unlike `IgnoreRegex`, since there's nothing to fall
back to. `QuickClone` (`src/GitTfs/Commands/QuickClone.cs`), which
subclasses `Clone` and constructs it directly with a `null` `InitBranch`,
also needs `RemoteOptions` threaded through its own constructor to satisfy
`Clone`'s new dependency.

This fix and the rest of this design's mechanics have been validated
end-to-end against the codebase (see "Validation" below).

### 5. Why "amend the tip commit" instead of "add one more commit on top"

The first version of this design proposed adding a small extra commit on top
of the last changeset's commit, containing just the `.gitkeep` changes. That
is unsafe under repeated `fetch` and was rejected:

`GitTfsRemote.InitHistory()` (`GitTfsRemote.cs:168-192`) determines where the
*next* fetch resumes from by calling
`Repository.GetLastParentTfsCommits(RemoteRef).FirstOrDefault()` — walking
the ref's ancestry for the most recent commit that actually carries a
`git-tfs-id` trailer, and setting `MaxCommitHash` to *that* commit, not to
the literal ref tip. The next fetch's `Apply()` then builds on top of that
TFS-tagged commit, bypassing anything stacked after it, and
`UpdateTfsHead` (`GitTfsRemote.cs:759`) force-moves the ref forward — silently
orphaning any plain commit that had been sitting on top. A `.gitkeep`-only
follow-up commit would vanish from tracked history the moment any later
`fetch` ran (and there would be no working-tree merge to incidentally rescue
it, e.g. on a bare mirror).

Amending the tip commit's tree in place, rather than stacking a new commit
after it, sidesteps this entirely: the replacement commit *is* the
legitimate "changeset N" commit as far as `git-tfs-id` bookkeeping is
concerned, so the next fetch's `InitHistory()` walk finds it directly with no
special-casing.

### 6. Safety boundary: never rewrite a commit from a previous invocation

The rewrite-in-place mechanism only ever targets a commit **created by the
current invocation** — gated on `fetchResult.NewChangesetCount > 0` (or the
`QuickFetch` snapshot's own always-fresh commit). If a `fetch` finds nothing
new (already up to date), reconciliation is skipped entirely, even if
`--keep-empty-folders` is newly passed on that invocation and would otherwise
want to backfill missing `.gitkeep`s onto existing commits.

This is non-negotiable: it guarantees the feature never rewrites a commit
that could already have been observed elsewhere — pushed, mirrored, pulled
by a teammate, or simply left over from an earlier invocation. The
consequence is a documented limitation: enabling `--keep-empty-folders` on an
already-cloned remote only affects changesets fetched from that point
forward; it does not retroactively fix up folders that were dropped by
earlier fetches. Retroactively rewriting arbitrary historical commits was
considered and rejected as disproportionately risky for what this feature
needs to deliver.

### 7. Placeholder file mechanics

`IGitTreeBuilder.Add(path, file, mode)` (`GitTreeBuilder.cs:22`) hashes a
blob from a real file on disk at `file` — there is no "add an empty blob by
content" shortcut in the LibGit2Sharp wrapper used here. One empty temp file
is created lazily and reused as the source path for every `.gitkeep` add
within a given reconciliation pass.

### 8. Error handling

Reconciliation is a best-effort enhancement layered on an otherwise-
successful fetch; it must never turn a successful fetch into a failed one.
If the raw listing query throws, or building/writing the replacement commit
fails, catch it, log a `Trace.TraceWarning` (matching the existing
swallow-and-warn style in `Clone.VerifyTfsPathToClone`), and leave the
already-made commit un-amended. The fetch that already succeeded is not
retroactively reported as failed over a placeholder-file concern.

### 9. Known limitations

- **Checkin round-trip is unhandled.** `DirectoryTidier` doesn't recognize
  `.gitkeep` as a non-real file. A folder holding only a `.gitkeep` in git,
  pushed back to TFS via `checkin`/`rcheckin`, will push the literal
  `.gitkeep` file into TFVC rather than being treated as empty. Deferred to
  a follow-up if it turns out to matter in practice.
- **A real, intentionally-versioned TFVC file named exactly `.gitkeep`** is
  indistinguishable from this feature's own placeholder across separate
  git-tfs invocations (no provenance tracking is persisted). The specific
  destructive case — a folder holding one real `.gitkeep` alone, which later
  gains its first other real file — would incorrectly delete that real
  file. Given how unusual intentionally versioning a file with this exact
  git-convention name in TFVC would be, this is documented as a known
  limitation rather than solved with provenance tracking.
- **No retroactive backfill**, per the safety boundary above.
- **Git notes from `--export` metadata on the amended commit are orphaned.**
  `GitTfsRemote.ProcessChangeset` (`GitTfsRemote.cs:525`) attaches a git note
  to the just-created commit's SHA when TFS metadata export is enabled. If
  that same commit is the one amended for `--keep-empty-folders`, the note
  stays attached to the original (now-unreferenced) SHA rather than the
  replacement. This only affects the narrow combination of two opt-in
  features on the exact last changeset of a fetch batch; documented as a
  known limitation rather than solved by re-creating notes on the new SHA.

## Validation

Every mechanism in this design (`EmptyFolderTracker`, the `RemoteOptions`/
`GitTfsRemote` wiring, the tip-commit-amend logic, the third early-return
fix, and the `--branches=all` propagation fix) was implemented against the
actual codebase and run against the fake-TFS-server integration harness
before being written up here — not just reasoned about. That surfaced one
infrastructure gap not caused by this feature but blocking its tests (see
next paragraph), and confirmed everything else in this document works
exactly as designed, including the resume-safety and safety-boundary
properties.

**The fake TFS harness (`src/GitTfs.VsFake/TfsHelper.VsFake.cs`) could not
run any test depending on `GetFullTree()`/`GetItems` before this feature.**
`FakeVersionControlServer.GetItems()` and `Changeset.VersionControlServer`
were unconditionally `throw new NotImplementedException()` stubs, as was
`TfsHelper.GetChangeset(int, IGitTfsRemote)` and
`FakeWorkspace.GetSpecificVersion(int, IEnumerable<IItem>, bool)` — the
entire chain `GetFullTree()` depends on. Nothing before this feature
exercised that chain: the only production caller, `TfsChangeset.GetTree()`
(via `CopyTree`), is only reached by `QuickFetch`/`clone -c`, which had zero
existing test coverage. All four had to be implemented for real (not
mocked) before any `--keep-empty-folders` integration test could do
anything but silently no-op inside this feature's own best-effort
try/catch. This is genuine test-infrastructure work, tracked as its own
task in the implementation plan, not a `--keep-empty-folders`-specific
concern — anything else that ever needs `GetFullTree()` benefits from it
too (e.g. `DirectoryTidier`'s checkin path, which has the same latent gap).

`FakeVersionControlServer.GetItems` is implemented by replaying the fake
script's changesets up to the requested changeset ID into a
"current live item per path" map (add/edit/branch/merge upserts the path,
delete removes it), then filtering to the requested root. It only supports
`TfsRecursionType.Full`, since that's the only value any production caller
passes — `None`/`OneLevel` still throw, matching this fake's existing
convention of only implementing what's exercised. Rename changes are
handled on a best-effort basis (treated as an add at the new path; the old
path is only cleaned up if the test script also issues an explicit delete
for it, since most scripted changes never set the optional `ItemId` needed
to correlate a rename's old and new path) — sufficient for every scenario
this feature's tests need, not a general-purpose rename-correctness fix.

One test-writing pitfall worth flagging for implementers: `IntegrationHelper
.SetupFake` replaces the fake script entirely on each call rather than
appending to it (`new Script().Tap(...).Save(...)`). Existing tests that
call it a second time (e.g. `FetchTests.AddNewCommitToFakeTfsServer`) get
away with supplying only the *new* changesets, because plain incremental
fetch only ever needs the delta since the last changeset. But
`GetFullTree()`'s replay needs the *entire* history to reconstruct current
state — a repeated-fetch test that calls `SetupFake` a second time with only
the new changeset will make the fake "forget" every earlier changeset ever
existed, corrupting the raw-listing snapshot. Any test exercising
`--keep-empty-folders` across repeated `SetupFake` calls must resupply the
full changeset history each time, not just the delta.

## Testing strategy

- **Unit tests for `EmptyFolderTracker`'s core algorithm**, pure and
  isolated: nested empty folders (only the deepest leaf gets a `.gitkeep`),
  a folder containing only empty subfolders, a folder with real-but-ignored
  content (must *not* get a `.gitkeep`), and removal detection (a folder
  that had a `.gitkeep` from a prior reconciliation and has since gained
  real content).
- **Integration tests** extending the existing fake-TFS-server harness
  (`src/GitTfsTest/Integration/CloneTests.cs` already has
  `ClonesEmptyProject`/`AssertEmptyWorkspace`): a clone with
  `--keep-empty-folders` plus a `.gitignore` reproducing the
  `docs`-empty/`packages`-ignored scenario end-to-end. These require the
  fake-harness fixes above as a prerequisite.
- **A regression test for the resume-safety issue found during design**:
  two sequential `fetch` invocations (supplying the full changeset history
  each time, per the `SetupFake` pitfall above), asserting the second
  invocation's resume point (`MaxChangesetId`) is correct and that
  `.gitkeep`s from the first invocation's reconciliation both survive
  (for folders still empty) and get removed (for a folder that gained real
  content) in the second invocation's tree.
- **A test locking in the safety boundary**: running `fetch
  --keep-empty-folders` when there is nothing new to fetch must not alter
  any existing commit SHA.
- **A test for `--branches=all` propagation**: cloning with
  `--branches=all --keep-empty-folders` where the auto-discovered branch
  (not the trunk) has an empty folder, asserting that branch's own git tree
  contains the `.gitkeep`.
