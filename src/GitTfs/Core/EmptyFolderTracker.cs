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
