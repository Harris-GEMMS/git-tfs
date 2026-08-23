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
    }
}
