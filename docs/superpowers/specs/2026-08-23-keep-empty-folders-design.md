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
(`clone`, `fetch`, `init`, `branch`, ...). Exposed as
`RemoteOptions.KeepEmptyFolders` (bool), read directly by the fetch pipeline
for that invocation only — no config persistence.

Placeholder filename is fixed as `.gitkeep`, not configurable.

### 2. Core algorithm: `EmptyFolderTracker`

A new class, `src/GitTfs/Core/EmptyFolderTracker.cs`, the conceptual mirror
of `DirectoryTidier` (which solves the reverse problem for checkin).

**`GetGitKeeps(IEnumerable<TfsTreeEntry> allItems)`** — the only method
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

`GetGitKeeps` only computes the "should have a `.gitkeep`" set — a one-shot,
in-memory, bottom-up walk over the raw listing, no TFS server calls beyond
the one listing query described next. The caller (`GitTfsRemote`) derives
both the add set and the remove set as a plain set difference against the
`.gitkeep` paths already present in the commit's current tree: paths in
"should have" but not in "currently has" are adds; paths in "currently has"
but not in "should have" are removes. No separate removal algorithm is
needed.

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

1. After the normal fetch loop finishes, `MaxCommitHash`/`MaxChangesetId`
   (`GitTfsRemote.cs:149-164`) point at the real last-fetched changeset's
   commit.
2. Issue one raw listing query at that exact changeset, files and folders,
   unfiltered — the same root-path / subtree-union logic
   `TfsChangeset.GetFullTree()` already uses for `Summary.Remote.TfsSubtreePaths`,
   so multi-subtree remotes are handled without extra design. (When this
   invocation happened to go through `CopyTree`/`QuickFetch`, that path has
   already fetched an equivalent full listing via `GetFullTree()` — the
   implementation should reuse it rather than querying twice.)
3. Run `EmptyFolderTracker.GetGitKeeps(...)` against it, diff against the
   `.gitkeep` paths already present in the commit's current tree (see set
   difference above), and collect the add/remove set.
4. If empty, done — no-op, no new commit object.
5. If not empty, build a *replacement* commit for that same tip: identical
   parents, author, committer, and message, but a tree with the
   `.gitkeep` adds/removes folded in. Call `UpdateTfsHead(newSha,
   MaxChangesetId)` to point the ref at the replacement.

Cost is now flat: one full listing query per `clone`/`fetch` invocation,
independent of how many changesets were replayed to produce that invocation's
final commit. This runs per-remote — each `GitTfsRemote` (each branch, in a
`--branches=all` clone) tracks its own `MaxCommitHash`/`RemoteRef`
independently, so multi-branch clones compose naturally with no extra
coordination.

### 4. Why "amend the tip commit" instead of "add one more commit on top"

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

### 5. Safety boundary: never rewrite a commit from a previous invocation

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

### 6. Placeholder file mechanics

`IGitTreeBuilder.Add(path, file, mode)` (`GitTreeBuilder.cs:22`) hashes a
blob from a real file on disk at `file` — there is no "add an empty blob by
content" shortcut in the LibGit2Sharp wrapper used here. One empty temp file
is created lazily and reused as the source path for every `.gitkeep` add
within a given reconciliation pass.

### 7. Error handling

Reconciliation is a best-effort enhancement layered on an otherwise-
successful fetch; it must never turn a successful fetch into a failed one.
If the raw listing query throws, or building/writing the replacement commit
fails, catch it, log a `Trace.TraceWarning` (matching the existing
swallow-and-warn style in `Clone.VerifyTfsPathToClone`), and leave the
already-made commit un-amended. The fetch that already succeeded is not
retroactively reported as failed over a placeholder-file concern.

### 8. Known limitations

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
  `docs`-empty/`packages`-ignored scenario end-to-end.
- **A regression test for the resume-safety issue found during design**:
  two sequential `fetch` invocations, asserting the second invocation's
  resume point (`MaxChangesetId`) is correct and that `.gitkeep`s from the
  first invocation's reconciliation survive into the second invocation's
  tree.
- **A test locking in the safety boundary**: running `fetch
  --keep-empty-folders` when there is nothing new to fetch must not alter
  any existing commit SHA.
