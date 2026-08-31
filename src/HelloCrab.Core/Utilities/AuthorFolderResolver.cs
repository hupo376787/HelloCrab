namespace HelloCrab.Core.Utilities;

public static class AuthorFolderResolver
{
    public sealed record Resolution(
        string FolderPath,
        string PreferredFolderPath,
        string? PreviousFolderPath,
        bool Renamed,
        string? RenameError)
    {
        public bool UsesPreferredFolder
            => FolderPath.Equals(PreferredFolderPath, StringComparison.Ordinal);
    }

    public static string Resolve(
        string platformDownloadRoot,
        string? authorName,
        string? authorId)
        => ResolveDetailed(
            platformDownloadRoot,
            authorName,
            authorId,
            updateAuthorNickname: false).FolderPath;

    public static Resolution ResolveDetailed(
        string platformDownloadRoot,
        string? authorName,
        string? authorId,
        bool updateAuthorNickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformDownloadRoot);

        var preferredFolder = Path.Combine(
            platformDownloadRoot,
            FileNameHelper.BuildAuthorFolderName(authorName, authorId));
        if (Directory.Exists(preferredFolder))
            return new Resolution(preferredFolder, preferredFolder, null, false, null);

        var idSuffix = FileNameHelper.BuildAuthorFolderIdSuffix(authorId);
        if (string.IsNullOrWhiteSpace(idSuffix) || !Directory.Exists(platformDownloadRoot))
            return new Resolution(preferredFolder, preferredFolder, null, false, null);

        try
        {
            // 作者改名后，沿用平台目录内以同一完整作者 ID 结尾的旧目录。
            // 只匹配规范的“(ID)”后缀，避免 ID 为 123 时误命中 9123。
            var existingFolder = Directory
                .EnumerateDirectories(platformDownloadRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).EndsWith(idSuffix, StringComparison.Ordinal))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(existingFolder))
                return new Resolution(preferredFolder, preferredFolder, null, false, null);

            if (!updateAuthorNickname)
                return new Resolution(existingFolder, preferredFolder, existingFolder, false, null);

            try
            {
                Directory.Move(existingFolder, preferredFolder);
                return new Resolution(preferredFolder, preferredFolder, existingFolder, true, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Resolution(existingFolder, preferredFolder, existingFolder, false, ex.Message);
            }
        }
        catch (IOException)
        {
            return new Resolution(preferredFolder, preferredFolder, null, false, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new Resolution(preferredFolder, preferredFolder, null, false, null);
        }
    }
}
