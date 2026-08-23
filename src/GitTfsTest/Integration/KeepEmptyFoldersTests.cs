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
