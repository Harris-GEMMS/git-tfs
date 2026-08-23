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
