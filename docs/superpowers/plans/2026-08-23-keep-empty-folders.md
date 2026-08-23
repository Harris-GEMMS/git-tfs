# --keep-empty-folders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `--keep-empty-folders` option to `clone`/`fetch`/`branch` that adds a `.gitkeep` placeholder to TFVC folders that are genuinely empty, so they survive into git, without touching folders whose only content was excluded by ignore rules — including when branches are auto-discovered via `--branches=all`.

**Architecture:** A new, pure, unit-testable `EmptyFolderTracker` class computes which folders need a `.gitkeep` from a raw (unfiltered) TFS item listing. `GitTfsRemote` calls it exactly once at the end of a `clone`/`fetch` invocation — never per changeset — comparing the result against the just-created tip commit's tree and, if anything differs, replacing that tip commit in place (same parents/author/message, amended tree) rather than stacking a new commit on top. Because the fake TFS test harness never previously supported the TFS query this depends on, this plan starts with a prerequisite task fixing that harness. A separate wiring gap (the option not reaching branches auto-discovered by `--branches=all`) is fixed as its own task.

**Every task below has already been implemented once, end-to-end, against this codebase, and reverted** (as a validation spike during design) — the code in each task's steps is exact and known to compile and pass, not a first draft.

**Tech Stack:** C# (.NET Framework 4.8), LibGit2Sharp, xunit + Moq, the repo's fake-TFS-server integration test harness (`IntegrationHelper`, `GitTfs.VsFake`).

**Spec:** `docs/superpowers/specs/2026-08-23-keep-empty-folders-design.md`

## Global Constraints

- Placeholder filename is fixed as `.gitkeep`, not configurable.
- The option is a plain per-invocation flag — never persisted to git config.
- Emptiness is judged against the **raw** TFVC item listing, never the ignore/`.gitignore`-filtered one. A folder whose only content is excluded by `--ignore-regex`/`.gitignore` is not empty and must not get a `.gitkeep`.
- Reconciliation runs **exactly once per `clone`/`fetch` invocation**, never per changeset — no changes to `ChangeSieve` or `TfsChangeset.Apply()`/`CopyTree()`.
- The tip-commit rewrite mechanism only ever targets a commit **created by the current invocation** (gated on at least one changeset having been fetched this run). It must never rewrite a commit that could already have been observed elsewhere. If nothing new was fetched, reconciliation is skipped entirely.
- Reconciliation is best-effort: any failure in it must be caught and logged as a warning, never allowed to fail an otherwise-successful fetch.
- `--keep-empty-folders` must reach every remote created during a `--branches=all` clone, not just the trunk.

---

### Task 1: `EmptyFolderTracker` core algorithm

**Files:**
- Create: `src/GitTfs/Core/EmptyFolderTracker.cs`
- Test: `src/GitTfsTest/Core/EmptyFolderTrackerTests.cs`

**Interfaces:**
- Produces: `EmptyFolderTracker.GetGitKeepPaths(IEnumerable<TfsTreeEntry> allItems) : IEnumerable<string>` — the git paths (e.g. `"docs/.gitkeep"`, or `".gitkeep"` for a fully-empty repo root) that need a placeholder, given a raw, unfiltered listing of every TFVC file and folder item.
- Produces: `EmptyFolderTracker.IsGitKeepPath(string gitPath) : bool` — true for `.gitkeep` at the root or `.../.gitkeep` at any depth.
- Consumes: `GitTfs.Core.TfsTreeEntry` (existing — `.FullName` is the git path, `.Item.ItemType` is `TfsItemType.File` or `TfsItemType.Folder`), `GitTfs.Core.TfsInterop.TfsItemType` (existing).

- [ ] **Step 1: Write the failing tests**

Create `src/GitTfsTest/Core/EmptyFolderTrackerTests.cs`:

```csharp
using Xunit;
using GitTfs.Core;
using GitTfs.Core.TfsInterop;
using Moq;

namespace GitTfs.Test.Core
{
    public class EmptyFolderTrackerTests : BaseTest
    {
        private readonly MockRepository mocks = new MockRepository(MockBehavior.Default);

        [Fact]
        public void NoItemsMeansNoGitKeeps() =>
            Assert.Empty(EmptyFolderTracker.GetGitKeepPaths(Array.Empty<TfsTreeEntry>()));

        [Fact]
        public void FolderWithNoChildrenGetsAGitKeep()
        {
            var items = new[]
            {
                item(TfsItemType.Folder, "docs"),
            };

            Assert.Equal(new[] { "docs/.gitkeep" }, EmptyFolderTracker.GetGitKeepPaths(items));
        }

        [Fact]
        public void FolderWithAFileDoesNotGetAGitKeep()
        {
            var items = new[]
            {
                item(TfsItemType.Folder, "packages"),
                item(TfsItemType.File,   "packages/Newtonsoft.Json.1.0.0.nupkg"),
            };

            Assert.Empty(EmptyFolderTracker.GetGitKeepPaths(items));
        }

        [Fact]
        public void OnlyTheDeepestFolderInANestedEmptyChainGetsAGitKeep()
        {
            var items = new[]
            {
                item(TfsItemType.Folder, "a"),
                item(TfsItemType.Folder, "a/b"),
                item(TfsItemType.Folder, "a/b/c"),
            };

            Assert.Equal(new[] { "a/b/c/.gitkeep" }, EmptyFolderTracker.GetGitKeepPaths(items));
        }

        [Fact]
        public void EachEmptyBranchOfATreeGetsItsOwnGitKeep()
        {
            var items = new[]
            {
                item(TfsItemType.Folder, "a"),
                item(TfsItemType.Folder, "a/left"),
                item(TfsItemType.Folder, "a/right"),
            };

            Assert.Equal(
                new[] { "a/left/.gitkeep", "a/right/.gitkeep" },
                EmptyFolderTracker.GetGitKeepPaths(items).OrderBy(x => x));
        }

        [Fact]
        public void AFolderThatHasOnlyAnEmptySubfolderDoesNotAlsoGetItsOwnGitKeep()
        {
            var items = new[]
            {
                item(TfsItemType.Folder, "a"),
                item(TfsItemType.Folder, "a/b"),
            };

            Assert.Equal(new[] { "a/b/.gitkeep" }, EmptyFolderTracker.GetGitKeepPaths(items));
        }

        [Fact]
        public void AnEntirelyEmptyProjectGetsAGitKeepAtTheRoot()
        {
            var items = new[]
            {
                item(TfsItemType.Folder, ""),
            };

            Assert.Equal(new[] { ".gitkeep" }, EmptyFolderTracker.GetGitKeepPaths(items));
        }

        [Fact]
        public void IsGitKeepPathRecognizesRootAndNestedPlaceholdersOnly()
        {
            Assert.True(EmptyFolderTracker.IsGitKeepPath(".gitkeep"));
            Assert.True(EmptyFolderTracker.IsGitKeepPath("docs/.gitkeep"));
            Assert.False(EmptyFolderTracker.IsGitKeepPath("docs/readme.txt"));
        }

        private TfsTreeEntry item(TfsItemType itemType, string gitPath) =>
            new TfsTreeEntry(gitPath, mocks.OneOf<IItem>().Tap(mockItem => Mock.Get(mockItem).Setup(x => x.ItemType).Returns(itemType)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~EmptyFolderTrackerTests"`
Expected: build error — `EmptyFolderTracker` does not exist yet.

- [ ] **Step 3: Write the implementation**

Create `src/GitTfs/Core/EmptyFolderTracker.cs`:

```csharp
using GitTfs.Core.TfsInterop;

namespace GitTfs.Core
{
    public static class EmptyFolderTracker
    {
        private const string PlaceholderFileName = ".gitkeep";

        // A folder needs a placeholder only if it has zero children of any kind (file or
        // folder). A folder whose only children are other empty folders does not also need
        // one - the deepest empty folder's own placeholder already implies every ancestor
        // directory exists in the git tree.
        public static IEnumerable<string> GetGitKeepPaths(IEnumerable<TfsTreeEntry> allItems)
        {
            var items = allItems.ToList();
            var childCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in items)
            {
                var parent = GetParentPath(entry.FullName);
                if (parent == null)
                    continue;
                childCounts[parent] = childCounts.TryGetValue(parent, out var count) ? count + 1 : 1;
            }

            foreach (var entry in items)
            {
                if (entry.Item.ItemType != TfsItemType.Folder)
                    continue;
                if (!childCounts.ContainsKey(entry.FullName))
                    yield return CombineWithPlaceholder(entry.FullName);
            }
        }

        public static bool IsGitKeepPath(string gitPath) =>
            gitPath == PlaceholderFileName || gitPath.EndsWith("/" + PlaceholderFileName, StringComparison.OrdinalIgnoreCase);

        private static string CombineWithPlaceholder(string folderPath) =>
            string.IsNullOrEmpty(folderPath) ? PlaceholderFileName : folderPath + "/" + PlaceholderFileName;

        private static string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null; // the root has no parent
            var slashIndex = path.LastIndexOf('/');
            return slashIndex < 0 ? string.Empty : path.Substring(0, slashIndex);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~EmptyFolderTrackerTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/GitTfs/Core/EmptyFolderTracker.cs src/GitTfsTest/Core/EmptyFolderTrackerTests.cs
git commit -m "feat: add EmptyFolderTracker to find TFVC folders with no real content"
```

---

### Task 2: Fake TFS harness prerequisite — implement `GetItems` and friends

The entire feature depends on `ITfsChangeset.GetFullTree()`, which calls
`IChangeset.VersionControlServer.GetItems(...)`. Before this task, the fake
TFS server used by every integration test (`GitTfs.VsFake`) could not answer
that call at all — not because of anything this feature does wrong, but
because nothing before it ever exercised that code path. `GetFullTree()`'s
only production caller, `TfsChangeset.GetTree()` (via `CopyTree`), is only
reached by `QuickFetch`/`clone -c`, which had zero existing test coverage.

This task fixes that, verified by a regression test for `QuickFetch` itself
(independent of `--keep-empty-folders`), so Task 3's integration tests have
a working foundation to build on.

**Files:**
- Modify: `src/GitTfs.VsFake/TfsHelper.VsFake.cs`
- Test: `src/GitTfsTest/Integration/CloneTests.cs` (add one test)

**Interfaces:**
- Fixes (all pre-existing, in `src/GitTfs.VsFake/TfsHelper.VsFake.cs`):
  `TfsHelper.Changeset.VersionControlServer` (was throwing),
  `TfsHelper.GetChangeset(int changesetId, IGitTfsRemote remote)` (was
  throwing), `TfsHelper.FakeVersionControlServer.GetItems(string, int,
  TfsRecursionType)` (was throwing), `TfsHelper.FakeWorkspace
  .GetSpecificVersion(int, IEnumerable<IItem>, bool)` (was throwing).
- No production (`src/GitTfs`) code changes in this task.

- [ ] **Step 1: Write the failing test**

Add to `src/GitTfsTest/Integration/CloneTests.cs` (anywhere in the class,
e.g. just before `CloneWithMixedUpCase`):

```csharp
        [FactExceptOnUnix]
        public void CloneFromAGivenChangesetUsesASingleSnapshot()
        {
            h.SetupFake(r =>
            {
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "Add a folder and a file", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/Folder")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/Folder/File.txt", "File contents")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/README", "tldr");
            });

            h.Run("clone", h.TfsUrl, "$/MyProject", "MyProject", "--from=2");

            h.AssertFileInWorkspace("MyProject", "Folder/File.txt", "File contents");
            h.AssertFileInWorkspace("MyProject", "README", "tldr");
            Assert.Equal(1, h.GetCommitCount("MyProject"));
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~CloneFromAGivenChangesetUsesASingleSnapshot"`
Expected: FAIL with `System.NotImplementedException` (surfaced as a
`GitTfsException` wrapping it) from `FakeWorkspace.GetSpecificVersion`.

- [ ] **Step 3: Fix `Changeset.VersionControlServer`**

In `src/GitTfs.VsFake/TfsHelper.VsFake.cs`, inside the private `Changeset`
class, change:

```csharp
            public IVersionControlServer VersionControlServer => throw new NotImplementedException();
```

to:

```csharp
            public IVersionControlServer VersionControlServer => _versionControlServer;
```

- [ ] **Step 4: Implement `TfsHelper.GetChangeset(int, IGitTfsRemote)`**

In the same file, change:

```csharp
        public ITfsChangeset GetChangeset(int changesetId, IGitTfsRemote remote) => throw new NotImplementedException();
```

to:

```csharp
        public ITfsChangeset GetChangeset(int changesetId, IGitTfsRemote remote) => BuildTfsChangeset(_script.Changesets.First(c => c.Id == changesetId), remote);
```

(this mirrors the existing `GetLatestChangeset(IGitTfsRemote remote)` right
above it, just filtering by exact `Id` instead of taking the last one)

- [ ] **Step 5: Implement `FakeVersionControlServer.GetItems`**

In the same file, inside the private `FakeVersionControlServer` class,
change:

```csharp
            public IItem[] GetItems(string itemPath, int changesetNumber, TfsRecursionType recursionType) => throw new NotImplementedException();
```

to:

```csharp
            public IItem[] GetItems(string itemPath, int changesetNumber, TfsRecursionType recursionType)
            {
                if (recursionType != TfsRecursionType.Full)
                    throw new NotImplementedException();

                var liveItems = new Dictionary<string, (ScriptedChangeset Changeset, ScriptedChange Change)>(StringComparer.InvariantCultureIgnoreCase);
                foreach (var changeset in _script.Changesets.Where(cs => cs.Id <= changesetNumber).OrderBy(cs => cs.Id))
                {
                    foreach (var change in changeset.Changes)
                    {
                        if (change.ChangeType.IncludesOneOf(TfsChangeType.Delete))
                            liveItems.Remove(change.RepositoryPath);
                        else
                            liveItems[change.RepositoryPath] = (changeset, change);
                    }
                }

                var root = itemPath.TrimEnd('/');
                return liveItems
                    .Where(kv => string.Equals(kv.Key, root, StringComparison.InvariantCultureIgnoreCase)
                              || kv.Key.StartsWith(root + "/", StringComparison.InvariantCultureIgnoreCase))
                    .Select(kv => (IItem)new Change(this, kv.Value.Changeset, kv.Value.Change))
                    .ToArray();
            }
```

Only `TfsRecursionType.Full` is implemented — that's the only value any
production caller passes (`TfsChangeset.GetFullTree()`), matching this
fake's existing convention of leaving unexercised paths as `throw new
NotImplementedException()`. Rename changes are handled on a best-effort
basis: they upsert the new path but don't clean up the old one unless the
test script also issues an explicit delete for it (most scripted changes
never set the optional `ItemId` needed to correlate a rename's before/after
path) — sufficient for this feature's tests, not a general rename fix.

- [ ] **Step 6: Implement `FakeWorkspace.GetSpecificVersion(int, IEnumerable<IItem>, bool)`**

In the same file, inside the private `FakeWorkspace` class, change:

```csharp
            public void GetSpecificVersion(int changesetId, IEnumerable<IItem> items, bool noParallel) => throw new NotImplementedException();
```

to:

```csharp
            public void GetSpecificVersion(int changesetId, IEnumerable<IItem> items, bool noParallel)
            {
                var repositoryRoot = _repositoryRoot.ToLower();
                if (!repositoryRoot.EndsWith("/")) repositoryRoot += "/";
                foreach (var item in items)
                {
                    if (item.ItemType == TfsItemType.File)
                    {
                        var outPath = Path.Combine(_directory, item.ServerItem.ToLower().Replace(repositoryRoot, ""));
                        var outDir = Path.GetDirectoryName(outPath);
                        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                        using (var download = item.DownloadFile())
                            File.WriteAllText(outPath, File.ReadAllText(download.Path));
                    }
                }
            }
```

This mirrors the existing `GetSpecificVersion(int, IEnumerable<IChange>,
bool)` overload immediately above it in the same class, just operating on
`IItem` directly instead of unwrapping `change.Item`.

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~CloneFromAGivenChangesetUsesASingleSnapshot"`
Expected: PASS.

- [ ] **Step 8: Run the full test suite to check for regressions**

Run: `dotnet test src/GitTfs.sln`
Expected: PASS, no new failures.

- [ ] **Step 9: Commit**

```bash
git add src/GitTfs.VsFake/TfsHelper.VsFake.cs src/GitTfsTest/Integration/CloneTests.cs
git commit -m "test: implement fake TFS server GetItems support for QuickFetch"
```

---

### Task 3: CLI option, `GitTfsRemote` wiring, and first end-to-end test

**Files:**
- Modify: `src/GitTfs/Commands/RemoteOptions.cs`
- Modify: `src/GitTfs/Core/GitTfsRemote.cs` (`FetchWithMerge`, `quickFetch`, new private method)
- Test: Create `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs`

**Interfaces:**
- Consumes: `EmptyFolderTracker.GetGitKeepPaths`/`IsGitKeepPath` (Task 1). `ITfsChangeset.GetFullTree()`, `IGitRepository.GetCommit(string)`, `GitCommit.GetTree()`, `IGitRepository.GetTreeBuilder(string)`, `IGitTreeBuilder.Add/Remove/GetTree()`, `IGitRepository.Commit(LogEntry)`, `GitTfsRemote.UpdateTfsHead(string, int)` (all existing).
- Produces: `RemoteOptions.KeepEmptyFolders : bool` (new). `GitTfsRemote.ReconcileEmptyFoldersIfNeeded(ITfsChangeset, LogEntry)` (new private method, called from `quickFetch` and from `FetchWithMerge`).

- [ ] **Step 1: Write the failing integration test**

Create `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs`:

```csharp
using GitTfs.Core.TfsInterop;
using Xunit;
using Xunit.Abstractions;

namespace GitTfs.Test.Integration
{
    public class KeepEmptyFoldersTests : BaseTest, IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly IntegrationHelper h;

        public KeepEmptyFoldersTests(ITestOutputHelper output)
        {
            _output = output;
            h = new IntegrationHelper();
            _output.WriteLine("Repository in folder: " + h.Workdir);
        }

        public void Dispose() => h.Dispose();

        [FactExceptOnUnix]
        public void KeepsAGenuinelyEmptyFolderButNotOneWhoseContentIsIgnored()
        {
            string gitignoreFile = Path.Combine(h.Workdir, "gitignore");
            File.WriteAllText(gitignoreFile, "packages/\n");

            h.SetupFake(r =>
            {
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "Add docs and packages", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/docs")
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/packages")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/packages/Some.Package.1.0.0.nupkg", "binary contents")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/README", "tldr");
            });

            h.Run("clone", h.TfsUrl, "$/MyProject", "MyProject", "--keep-empty-folders", $"--gitignore={gitignoreFile}");

            h.AssertFileInWorkspace("MyProject", "docs/.gitkeep", "");
            h.AssertNoFileInWorkspace("MyProject", "packages/.gitkeep");
            h.AssertNoFileInWorkspace("MyProject", "packages/Some.Package.1.0.0.nupkg");
        }

        [FactExceptOnUnix]
        public void WithoutTheOptionNoGitKeepIsAdded()
        {
            h.SetupFake(r =>
            {
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "Add an empty folder", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/docs")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/README", "tldr");
            });

            h.Run("clone", h.TfsUrl, "$/MyProject", "MyProject");

            h.AssertNoFileInWorkspace("MyProject", "docs/.gitkeep");
        }
    }
}
```

Note: this test file does not use `IntegrationHelper.AssertTreeEntries` for
nested paths — that helper only enumerates entries immediately under the
given tree (it doesn't recurse into subtrees), so it can't verify a nested
path like `"docs/.gitkeep"` exists or doesn't. `AssertFileInWorkspace`/
`AssertNoFileInWorkspace` check the checked-out working directory instead,
which handles nested paths correctly since it's just filesystem access.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~KeepEmptyFoldersTests"`
Expected: `KeepsAGenuinelyEmptyFolderButNotOneWhoseContentIsIgnored` fails (`--keep-empty-folders` is not a recognized option). `WithoutTheOptionNoGitKeepIsAdded` should already pass (it exercises no new behavior) — that's fine, it's a guard for the next step.

- [ ] **Step 3: Add the CLI option**

In `src/GitTfs/Commands/RemoteOptions.cs`, add the option to the `OptionSet` (after the `no-gitignore` entry) and add the backing property:

```csharp
                    { "no-gitignore", "Do not use .gitignore to ignore files on download from TFS",
                        v => NoGitIgnore = v != null },
                    { "keep-empty-folders", "Add a .gitkeep placeholder to TFVC folders that have no real content, so they survive into git",
                        v => KeepEmptyFolders = v != null },
                    { "u|username=", "TFS username",
                        v => Username = v },
```

and, alongside the other properties:

```csharp
        public bool NoGitIgnore { get; set; }
        public bool KeepEmptyFolders { get; set; }
        public string Username { get; set; }
```

- [ ] **Step 4: Add the reconciliation method to `GitTfsRemote`**

In `src/GitTfs/Core/GitTfsRemote.cs`, insert this new private method immediately after the `RemoteRef` property (currently line 771, right before `private void DoGcIfNeeded()`):

```csharp
        private void ReconcileEmptyFoldersIfNeeded(ITfsChangeset changeset, LogEntry log)
        {
            if (!_remoteOptions.KeepEmptyFolders)
                return;

            List<string> neededGitKeepPaths;
            try
            {
                neededGitKeepPaths = EmptyFolderTracker.GetGitKeepPaths(changeset.GetFullTree()).ToList();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("warning: --keep-empty-folders: failed to list TFS items to determine empty folders (" + ex.Message + "). Skipping for this fetch.");
                return;
            }

            var commitSha = MaxCommitHash;
            var existingGitKeepPaths = Repository.GetCommit(commitSha).GetTree()
                .Select(entry => entry.FullName)
                .Where(EmptyFolderTracker.IsGitKeepPath)
                .ToList();

            var toAdd = neededGitKeepPaths.Except(existingGitKeepPaths, StringComparer.OrdinalIgnoreCase).ToList();
            var toRemove = existingGitKeepPaths.Except(neededGitKeepPaths, StringComparer.OrdinalIgnoreCase).ToList();
            if (toAdd.Count == 0 && toRemove.Count == 0)
                return;

            string placeholderFile = null;
            try
            {
                var treeBuilder = Repository.GetTreeBuilder(commitSha);
                if (toAdd.Count > 0)
                {
                    placeholderFile = Path.GetTempFileName();
                    foreach (var path in toAdd)
                        treeBuilder.Add(path, placeholderFile, LibGit2Sharp.Mode.NonExecutableFile);
                }
                foreach (var path in toRemove)
                    treeBuilder.Remove(path);

                // log.Log already carries the git-tfs-id trailer appended by the earlier
                // Commit(log) call for this same LogEntry - committing directly via
                // Repository.Commit (not the private Commit(LogEntry) wrapper) avoids
                // appending it a second time.
                log.Tree = treeBuilder.GetTree();
                var newCommit = Repository.Commit(log);
                UpdateTfsHead(newCommit.Sha, MaxChangesetId);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("warning: --keep-empty-folders: failed to add or remove .gitkeep placeholders (" + ex.Message + "). Skipping for this fetch.");
            }
            finally
            {
                if (placeholderFile != null)
                    File.Delete(placeholderFile);
            }
        }
```

- [ ] **Step 5: Call it from `quickFetch`**

In `src/GitTfs/Core/GitTfsRemote.cs`, change (around line 705):

```csharp
        private void quickFetch(ITfsChangeset changeset)
        {
            var log = CopyTree(MaxCommitHash, changeset);
            UpdateTfsHead(Commit(log), changeset.Summary.ChangesetId);
            DoGcIfNeeded();
        }
```

to:

```csharp
        private void quickFetch(ITfsChangeset changeset)
        {
            var log = CopyTree(MaxCommitHash, changeset);
            UpdateTfsHead(Commit(log), changeset.Summary.ChangesetId);
            ReconcileEmptyFoldersIfNeeded(changeset, log);
            DoGcIfNeeded();
        }
```

- [ ] **Step 6: Call it from `FetchWithMerge`, including the bounded-fetch cutoff**

In `src/GitTfs/Core/GitTfsRemote.cs`, in `FetchWithMerge` (currently lines 319-393):

1. Add two local variables right after `bool fetchRetrievedChangesets;` (before the `do` loop):

```csharp
            bool fetchRetrievedChangesets;
            ITfsChangeset lastChangeset = null;
            LogEntry lastLog = null;
            do
```

2. The `lastChangesetIdToFetch` cutoff check currently reads:

```csharp
                    fetchResult.NewChangesetCount++;
                    if (lastChangesetIdToFetch > 0 && changeset.Summary.ChangesetId > lastChangesetIdToFetch)
                        return fetchResult;
                    string parentCommitSha = null;
```

Change it to also reconcile whatever was committed by the *previous*
iteration before returning — this cutoff is a legitimate, complete stopping
point (unlike the merge-failure and rename-boundary returns elsewhere in
this loop, which are transient and are deliberately left untouched):

```csharp
                    fetchResult.NewChangesetCount++;
                    if (lastChangesetIdToFetch > 0 && changeset.Summary.ChangesetId > lastChangesetIdToFetch)
                    {
                        // The changeset that would come next is past the requested cutoff, but
                        // everything up to and including the previous changeset was committed
                        // as a legitimate, complete stopping point - reconcile it same as a
                        // normal end-of-fetch.
                        if (lastChangeset != null)
                            ReconcileEmptyFoldersIfNeeded(lastChangeset, lastLog);
                        return fetchResult;
                    }
                    string parentCommitSha = null;
```

3. Right after `var commitSha = ProcessChangeset(changeset, log);` (inside the `foreach`), add two lines:

```csharp
                    var commitSha = ProcessChangeset(changeset, log);
                    lastChangeset = changeset;
                    lastLog = log;
                    fetchResult.LastFetchedChangesetId = changeset.Summary.ChangesetId;
```

4. Change the method's final lines from:

```csharp
            } while (fetchRetrievedChangesets && latestChangesetId > fetchResult.LastFetchedChangesetId);
            return fetchResult;
        }
```

to:

```csharp
            } while (fetchRetrievedChangesets && latestChangesetId > fetchResult.LastFetchedChangesetId);

            if (lastChangeset != null)
                ReconcileEmptyFoldersIfNeeded(lastChangeset, lastLog);

            return fetchResult;
        }
```

The merge-changeset-failure return and the rename-boundary-pause return
elsewhere in this loop are deliberately left untouched — they represent
transient/incomplete states, and whatever they leave uncommitted this
invocation is simply reconciled by whichever later invocation actually
reaches one of the two complete stopping points above.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~KeepEmptyFoldersTests"`
Expected: PASS, both tests.

- [ ] **Step 8: Run the full test suite to check for regressions**

Run: `dotnet test src/GitTfs.sln`
Expected: PASS, no new failures.

- [ ] **Step 9: Commit**

```bash
git add src/GitTfs/Commands/RemoteOptions.cs src/GitTfs/Core/GitTfsRemote.cs src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs
git commit -m "feat: add --keep-empty-folders option to clone/fetch"
```

---

### Task 4: Regression test for repeated `fetch`

This is the scenario that surfaced the tip-commit-rewrite design during
brainstorming: a second `fetch` must resume from the right changeset,
`.gitkeep`s from the first fetch's reconciliation must survive into the
second fetch's tree, and a folder that gains real content after having had
a `.gitkeep` must lose it.

**Files:**
- Modify: `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs` (add one test method; no production code change expected)

**Interfaces:**
- Consumes: `IntegrationHelper.SetupFake`, `IntegrationHelper.RunIn`, `IntegrationHelper.GetCommitCount`.

- [ ] **Step 1: Write the test**

Add to `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs`:

```csharp
        [FactExceptOnUnix]
        public void GitKeepsSurviveAndResumePointStaysCorrectAcrossRepeatedFetch()
        {
            h.SetupFake(r =>
            {
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "Add two empty folders", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/docs")
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/more-docs")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/README", "tldr");
            });
            h.Run("clone", h.TfsUrl, "$/MyProject", "MyProject", "--keep-empty-folders");
            h.AssertFileInWorkspace("MyProject", "docs/.gitkeep", "");
            h.AssertFileInWorkspace("MyProject", "more-docs/.gitkeep", "");
            Assert.Equal(2, h.GetCommitCount("MyProject"));

            // IntegrationHelper.SetupFake REPLACES the fake TFS script on every call rather
            // than appending to it - so the second call must resupply the ENTIRE changeset
            // history (1, 2, and 3), not just the new delta. Plain incremental fetch would
            // work fine with just changeset 3 (it only ever needs the delta since the last
            // changeset), but reconciliation's GetFullTree() replays the fake script's full
            // history to reconstruct current raw state, and would otherwise "forget" that
            // "more-docs" was ever added, wrongly treating it as gone.
            h.SetupFake(r =>
            {
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "Add two empty folders", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/docs")
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/more-docs")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/README", "tldr");
                // This changeset both adds real content to "docs" (its .gitkeep must be
                // removed) and leaves "more-docs" untouched and still empty (its .gitkeep
                // must survive).
                r.Changeset(3, "Populate docs", DateTime.Parse("2012-01-03 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/docs/guide.md", "how to use this");
            });
            h.RunIn("MyProject", "pull", "--keep-empty-folders");

            h.AssertFileInWorkspace("MyProject", "docs/guide.md", "how to use this");
            h.AssertNoFileInWorkspace("MyProject", "docs/.gitkeep");
            h.AssertFileInWorkspace("MyProject", "more-docs/.gitkeep", "");
            Assert.Equal(3, h.GetCommitCount("MyProject"));
        }
```

Note: `pull` is used rather than `fetch` here (and in Task 5) because `pull`
runs fetch and then merges the remote ref into local HEAD
(`src/GitTfs/Commands/Pull.cs`) — plain `fetch` wouldn't update the local
working copy/HEAD that `AssertFileInWorkspace`/`GetCommitCount` (which
counts commits reachable from HEAD) check.

- [ ] **Step 2: Run the test**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~GitKeepsSurviveAndResumePointStaysCorrectAcrossRepeatedFetch"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs
git commit -m "test: add repeated-fetch regression test for --keep-empty-folders"
```

---

### Task 5: Safety-boundary test — an up-to-date fetch must not rewrite anything

**Files:**
- Modify: `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs` (add one test method; no production code change expected)

**Interfaces:**
- Consumes: `IntegrationHelper.RevParseCommit`.

- [ ] **Step 1: Write the test**

Add to `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs`:

```csharp
        [FactExceptOnUnix]
        public void FetchingWithNothingNewDoesNotRewriteTheExistingCommit()
        {
            h.SetupFake(r =>
            {
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "Add a file, no empty folders yet", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/README", "tldr");
            });
            h.Run("clone", h.TfsUrl, "$/MyProject", "MyProject");
            var shaBeforeFetch = h.RevParseCommit("MyProject", "refs/remotes/tfs/default").Sha;

            // Nothing new was added to the fake TFS server - turning the option on now,
            // with nothing to fetch, must be a no-op rather than rewriting history.
            h.RunIn("MyProject", "pull", "--keep-empty-folders");

            var shaAfterFetch = h.RevParseCommit("MyProject", "refs/remotes/tfs/default").Sha;
            Assert.Equal(shaBeforeFetch, shaAfterFetch);
            h.AssertNoFileInWorkspace("MyProject", ".gitkeep");
        }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~FetchingWithNothingNewDoesNotRewriteTheExistingCommit"`
Expected: PASS. If it fails with the SHA changing, check that `FetchWithMerge`'s early "already up to date" return (`if (MaxChangesetId >= latestChangesetId) return fetchResult;`) is reached before `lastChangeset`/`lastLog` are ever assigned, so the guard added in Task 3 Step 6 correctly skips reconciliation.

- [ ] **Step 3: Commit**

```bash
git add src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs
git commit -m "test: add safety-boundary test for --keep-empty-folders"
```

---

### Task 6: Propagate the option to `--branches=all`

> **⚠️ SUPERSEDED DURING IMPLEMENTATION — the approach described in this task
> was NOT what shipped.** This task's premise (that `--branches=all` silently
> drops the flag, fixed by threading `RemoteOptions` through `Clone`/
> `QuickClone` into a new `InitBranch.KeepEmptyFolders` property) turned out
> to be wrong on the facts. Writing the test first showed
> `clone --branches=all --keep-empty-folders` **already worked with no code
> changes**: `GitRepository.BuildRemote` resolves every `GitTfsRemote` through
> the DI container and never overrides `RemoteOptions`, so the singleton
> holding the parsed flag is already shared by trunk and auto-discovered
> branches alike. Adding an entry to `InitBranch.OptionSet` would have been
> dead code too — `InitBranch` has no `[Pluggable]` attribute, so its
> `OptionSet` is never parsed from a command line. The one real gap was a
> different entry point: standalone `git tfs branch --init --all`, whose
> `[Pluggable]` command is `Branch.cs`, which simply had no
> `--keep-empty-folders` option. What shipped is one file: `Branch` takes
> `RemoteOptions` as a constructor dependency (mirroring `Fetch`) and gains
> one `OptionSet` entry writing straight to that injected singleton. No
> changes to `Clone.cs`, `QuickClone.cs`, or `InitBranch.cs`. See the
> corrected §4 of
> `docs/superpowers/specs/2026-08-23-keep-empty-folders-design.md` for the
> full explanation. The steps below are retained as a record of the plan as
> written, not as instructions to follow.

`RemoteOptions` is a `[StructureMapSingleton]`, so `--keep-empty-folders`
parsed on `clone` is automatically visible to `Fetch`/`Init`. It is *not*
visible to remotes auto-discovered via `--branches=all`: `Clone.Run()` calls
`InitBranch.Run()` as a direct method call (no re-parsing of the command
line), and `InitBranch.InitFromDefaultRemote()` builds a fresh, disconnected
`RemoteOptions()` rather than using the injected singleton. Unlike
`--ignore-regex` (which `InitBranch` recovers from the trunk remote's
*persisted* config as a fallback), `--keep-empty-folders` is deliberately
never persisted, so there's nothing to fall back to — it needs explicit
propagation.

**Files:**
- Modify: `src/GitTfs/Commands/Clone.cs`
- Modify: `src/GitTfs/Commands/QuickClone.cs`
- Modify: `src/GitTfs/Commands/InitBranch.cs`
- Test: `src/GitTfsTest/Integration/CloneTests.cs` (add one test)

**Interfaces:**
- Produces: `InitBranch.KeepEmptyFolders : bool` (new property, plus a
  matching `--keep-empty-folders` `OptionSet` entry so `branch --init`
  supports the flag standalone too).
- Modifies constructors: `Clone(Globals, Fetch, Init, InitBranch,
  RemoteOptions)` (adds `RemoteOptions`), `QuickClone(Globals, Init,
  QuickFetch, RemoteOptions)` (adds `RemoteOptions`, threads it to `base(...)`).

- [ ] **Step 1: Write the failing test**

Add to `src/GitTfsTest/Integration/CloneTests.cs` (e.g. just before
`CloneWithMixedUpCase`):

```csharp
        [FactExceptOnUnix]
        public void BranchesAllPropagatesKeepEmptyFolders()
        {
            h.SetupFake(r =>
            {
                r.SetRootBranch("$/MyProject/Main");
                r.Changeset(1, "Project created from template", DateTime.Parse("2012-01-01 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject");
                r.Changeset(2, "First commit", DateTime.Parse("2012-01-02 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/Main")
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/Main/File.txt", "File contents");
                r.BranchChangeset(3, "create branch", DateTime.Parse("2012-01-02 12:12:14 -05:00"), fromBranch: "$/MyProject/Main", toBranch: "$/MyProject/Branch", rootChangesetId: 2)
                    .Change(TfsChangeType.Branch, TfsItemType.Folder, "$/MyProject/Branch")
                    .Change(TfsChangeType.Branch, TfsItemType.File, "$/MyProject/Branch/File.txt", "File contents");
                r.Changeset(4, "add empty folder to branch", DateTime.Parse("2012-01-02 12:12:15 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.Folder, "$/MyProject/Branch/docs");
            });

            h.Run("clone", h.TfsUrl, "$/MyProject/Main", "MyProject", "--branches=all", "--keep-empty-folders");

            h.AssertFileInWorkspace("MyProject", "File.txt", "File contents");
            var branchCommit = h.RevParseCommit("MyProject", "refs/remotes/tfs/Branch");
            Assert.NotNull(branchCommit);
            var branchTreeEntries = branchCommit.Tree.Select(e => e.Path).ToList();
            Assert.Contains("docs", branchTreeEntries);
        }
```

This asserts on the auto-discovered `Branch` remote's own tree (not the
trunk's) — `docs` can only appear as a tree entry there if something (the
`.gitkeep`) was actually placed inside it, since git never represents a
truly empty directory as a tree entry at all.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~BranchesAllPropagatesKeepEmptyFolders"`
Expected: FAIL — `docs` does not appear in the `Branch` remote's tree, because the option never reached it.

- [ ] **Step 3: Thread `RemoteOptions` through `Clone` and `QuickClone`**

In `src/GitTfs/Commands/Clone.cs`, change:

```csharp
        private readonly Fetch _fetch;
        private readonly Init _init;
        private readonly Globals _globals;
        private readonly InitBranch _initBranch;
        private bool _resumable;

        public Clone(Globals globals, Fetch fetch, Init init, InitBranch initBranch)
        {
            _fetch = fetch;
            _init = init;
            _globals = globals;
            _initBranch = initBranch;
            globals.GcCountdown = globals.GcPeriod;
        }
```

to:

```csharp
        private readonly Fetch _fetch;
        private readonly Init _init;
        private readonly Globals _globals;
        private readonly InitBranch _initBranch;
        private readonly RemoteOptions _remoteOptions;
        private bool _resumable;

        public Clone(Globals globals, Fetch fetch, Init init, InitBranch initBranch, RemoteOptions remoteOptions)
        {
            _fetch = fetch;
            _init = init;
            _globals = globals;
            _initBranch = initBranch;
            _remoteOptions = remoteOptions;
            globals.GcCountdown = globals.GcPeriod;
        }
```

Then, in the same file, where `Clone.Run()` calls into `InitBranch` for
`--branches=all`, change:

```csharp
                if (_fetch.BranchStrategy == BranchStrategy.All && _initBranch != null)
                {
                    _initBranch.CloneAllBranches = true;

                    retVal = _initBranch.Run();
                }
```

to:

```csharp
                if (_fetch.BranchStrategy == BranchStrategy.All && _initBranch != null)
                {
                    _initBranch.CloneAllBranches = true;
                    _initBranch.KeepEmptyFolders = _remoteOptions.KeepEmptyFolders;

                    retVal = _initBranch.Run();
                }
```

In `src/GitTfs/Commands/QuickClone.cs`, change:

```csharp
        public QuickClone(Globals globals, Init init, QuickFetch fetch)
            : base(globals, fetch, init, null)
        {
        }
```

to:

```csharp
        public QuickClone(Globals globals, Init init, QuickFetch fetch, RemoteOptions remoteOptions)
            : base(globals, fetch, init, null, remoteOptions)
        {
        }
```

- [ ] **Step 4: Add `KeepEmptyFolders` to `InitBranch`**

In `src/GitTfs/Commands/InitBranch.cs`, add the property alongside the
others:

```csharp
        public string IgnoreRegex { get; set; }
        public string ExceptRegex { get; set; }
        public bool KeepEmptyFolders { get; set; }
        public bool CloneAllBranches { get; set; }
```

Add the matching `OptionSet` entry (so `branch --init` supports the flag
standalone too, consistent with how `--ignore-regex` is treated here):

```csharp
                    { "except-regex=", "A regex of exceptions to ignore-regex", v => ExceptRegex = v},
                    { "keep-empty-folders", "Add a .gitkeep placeholder to TFVC folders that have no real content, so they survive into git", v => KeepEmptyFolders = v != null },
                    { "no-fetch", "Create the new TFS remote but don't fetch any changesets", v => NoFetch = (v != null) }
```

Apply it unconditionally in `InitFromDefaultRemote()` — no if/else fallback
needed here (unlike `IgnoreRegex`), since there's no persisted value to
fall back to:

```csharp
            _remoteOptions = new RemoteOptions();
            _remoteOptions.KeepEmptyFolders = KeepEmptyFolders;
            if (!string.IsNullOrWhiteSpace(TfsUsername))
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~BranchesAllPropagatesKeepEmptyFolders"`
Expected: PASS.

- [ ] **Step 6: Run the full test suite to check for regressions**

Run: `dotnet test src/GitTfs.sln`
Expected: PASS, no new failures.

- [ ] **Step 7: Commit**

```bash
git add src/GitTfs/Commands/Clone.cs src/GitTfs/Commands/QuickClone.cs src/GitTfs/Commands/InitBranch.cs src/GitTfsTest/Integration/CloneTests.cs
git commit -m "fix: propagate --keep-empty-folders to branches discovered via --branches=all"
```
