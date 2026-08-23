# --keep-empty-folders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `--keep-empty-folders` option to `clone`/`fetch`/`branch` that adds a `.gitkeep` placeholder to TFVC folders that are genuinely empty, so they survive into git, without touching folders whose only content was excluded by ignore rules.

**Architecture:** A new, pure, unit-testable `EmptyFolderTracker` class computes which folders need a `.gitkeep` from a raw (unfiltered) TFS item listing. `GitTfsRemote` calls it exactly once at the end of a `clone`/`fetch` invocation — never per changeset — comparing the result against the just-created tip commit's tree and, if anything differs, replacing that tip commit in place (same parents/author/message, amended tree) rather than stacking a new commit on top.

**Tech Stack:** C# (.NET Framework 4.8), LibGit2Sharp, xunit + Moq, the repo's fake-TFS-server integration test harness (`IntegrationHelper`).

**Spec:** `docs/superpowers/specs/2026-08-23-keep-empty-folders-design.md`

## Global Constraints

- Placeholder filename is fixed as `.gitkeep`, not configurable.
- The option is a plain per-invocation flag — never persisted to git config.
- Emptiness is judged against the **raw** TFVC item listing, never the ignore/`.gitignore`-filtered one. A folder whose only content is excluded by `--ignore-regex`/`.gitignore` is not empty and must not get a `.gitkeep`.
- Reconciliation runs **exactly once per `clone`/`fetch` invocation**, never per changeset — no changes to `ChangeSieve` or `TfsChangeset.Apply()`/`CopyTree()`.
- The tip-commit rewrite mechanism only ever targets a commit **created by the current invocation** (gated on at least one changeset having been fetched this run). It must never rewrite a commit that could already have been observed elsewhere. If nothing new was fetched, reconciliation is skipped entirely.
- Reconciliation is best-effort: any failure in it must be caught and logged as a warning, never allowed to fail an otherwise-successful fetch.

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

### Task 2: CLI option, `GitTfsRemote` wiring, and first end-to-end test

**Files:**
- Modify: `src/GitTfs/Commands/RemoteOptions.cs`
- Modify: `src/GitTfs/Core/GitTfsRemote.cs:319-393` (`FetchWithMerge`), `:705-710` (`quickFetch`), add new private method after `:771` (`RemoteRef`)
- Test: Create `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs`

**Interfaces:**
- Consumes: `EmptyFolderTracker.GetGitKeepPaths`/`IsGitKeepPath` (Task 1). `ITfsChangeset.GetFullTree()`, `IGitRepository.GetCommit(string)`, `GitCommit.GetTree()`, `IGitRepository.GetTreeBuilder(string)`, `IGitTreeBuilder.Add/Remove/GetTree()`, `IGitRepository.Commit(LogEntry)`, `GitTfsRemote.UpdateTfsHead(string, int)` (all existing).
- Produces: `RemoteOptions.KeepEmptyFolders : bool` (new, consumed only inside `GitTfsRemote`). `GitTfsRemote.ReconcileEmptyFoldersIfNeeded(ITfsChangeset, LogEntry)` (new private method — no other task calls it directly).

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
            h.AssertTreeEntries("MyProject", "HEAD", ".gitignore", "README", "docs/.gitkeep");
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

            h.AssertTreeEntries("MyProject", "HEAD", "README");
        }
    }
}
```

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

- [ ] **Step 6: Call it once at the end of `FetchWithMerge`**

In `src/GitTfs/Core/GitTfsRemote.cs`, in `FetchWithMerge` (currently lines 319-393):

1. Add two local variables right after `bool fetchRetrievedChangesets;` (before the `do` loop):

```csharp
            bool fetchRetrievedChangesets;
            ITfsChangeset lastChangeset = null;
            LogEntry lastLog = null;
            do
```

2. Right after `var commitSha = ProcessChangeset(changeset, log);` (inside the `foreach`), add:

```csharp
                    var commitSha = ProcessChangeset(changeset, log);
                    lastChangeset = changeset;
                    lastLog = log;
                    fetchResult.LastFetchedChangesetId = changeset.Summary.ChangesetId;
```

(this replaces the existing `var commitSha = ProcessChangeset(changeset, log);` / `fetchResult.LastFetchedChangesetId = changeset.Summary.ChangesetId;` pair — just insert the two new lines between them)

3. Change the method's final line from:

```csharp
            } while (fetchRetrievedChangesets && latestChangesetId > fetchResult.LastFetchedChangesetId);
            return fetchResult;
```

to:

```csharp
            } while (fetchRetrievedChangesets && latestChangesetId > fetchResult.LastFetchedChangesetId);

            if (lastChangeset != null)
                ReconcileEmptyFoldersIfNeeded(lastChangeset, lastLog);

            return fetchResult;
```

Note: the two early `return fetchResult;` statements inside the loop (merge failure, rename-boundary pause) are deliberately left untouched — reconciliation only runs on the loop's normal completion, per the Global Constraints safety boundary. Those paths simply pick up reconciliation on whichever later call actually completes normally.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~KeepEmptyFoldersTests"`
Expected: PASS, both tests.

- [ ] **Step 8: Run the full test suite to check for regressions**

Run: `dotnet test src/GitTfs.sln`
Expected: PASS, no new failures (in particular, all pre-existing `CloneTests`/`FetchTests` still pass unchanged, since `--keep-empty-folders` defaults to off).

- [ ] **Step 9: Commit**

```bash
git add src/GitTfs/Commands/RemoteOptions.cs src/GitTfs/Core/GitTfsRemote.cs src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs
git commit -m "feat: add --keep-empty-folders option to clone/fetch"
```

---

### Task 3: Regression test for repeated `fetch`

This is the scenario that surfaced the tip-commit-rewrite design during brainstorming: a second `fetch` must resume from the right changeset, `.gitkeep`s from the first fetch's reconciliation must survive into the second fetch's tree, and a folder that gains real content after having had a `.gitkeep` must lose it.

**Files:**
- Modify: `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs` (add one test method; no production code change expected)

**Interfaces:**
- Consumes: `IntegrationHelper.SetupFake` (called a second time, to add changesets to the fake TFS server after the initial clone — see `FetchTests.AddNewCommitToFakeTfsServer` for the existing precedent), `IntegrationHelper.RunIn`, `IntegrationHelper.GetCommitCount`.

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

            // This changeset both adds real content to "docs" (its .gitkeep must be removed)
            // and leaves "more-docs" untouched and still empty (its .gitkeep must survive).
            h.SetupFake(r =>
                r.Changeset(3, "Populate docs", DateTime.Parse("2012-01-03 12:12:12 -05:00"))
                    .Change(TfsChangeType.Add, TfsItemType.File, "$/MyProject/docs/guide.md", "how to use this"));
            h.RunIn("MyProject", "pull", "--keep-empty-folders");

            h.AssertFileInWorkspace("MyProject", "docs/guide.md", "how to use this");
            h.AssertNoFileInWorkspace("MyProject", "docs/.gitkeep");
            h.AssertFileInWorkspace("MyProject", "more-docs/.gitkeep", "");
            h.AssertTreeEntries("MyProject", "refs/remotes/tfs/default", "README", "docs/guide.md", "more-docs/.gitkeep");
            Assert.Equal(3, h.GetCommitCount("MyProject"));
        }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test src/GitTfs.sln --filter "FullyQualifiedName~GitKeepsSurviveAndResumePointStaysCorrectAcrossRepeatedFetch"`
Expected: PASS. If it fails, the most likely cause is the resume-point bug described in the spec ("Why amend the tip commit instead of adding one on top") reappearing — re-check that `ReconcileEmptyFoldersIfNeeded` calls `UpdateTfsHead` (not just `Repository.UpdateRef`) so `MaxCommitHash`/`MaxChangesetId` and the ref all move together.

- [ ] **Step 3: Commit**

```bash
git add src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs
git commit -m "test: add repeated-fetch regression test for --keep-empty-folders"
```

---

### Task 4: Safety-boundary test — an up-to-date fetch must not rewrite anything

**Files:**
- Modify: `src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs` (add one test method; no production code change expected)

**Interfaces:**
- Consumes: `IntegrationHelper.RevParseCommit` (to capture and compare a commit SHA before/after).

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
Expected: PASS. If it fails with the SHA changing, check that `FetchWithMerge`'s early `return fetchResult;` at the "already up to date" check (`if (MaxChangesetId >= latestChangesetId) return fetchResult;`) is reached before `lastChangeset`/`lastLog` are ever assigned, so the `if (lastChangeset != null)` guard added in Task 2 Step 6 correctly skips reconciliation.

- [ ] **Step 3: Commit**

```bash
git add src/GitTfsTest/Integration/KeepEmptyFoldersTests.cs
git commit -m "test: add safety-boundary test for --keep-empty-folders"
```
