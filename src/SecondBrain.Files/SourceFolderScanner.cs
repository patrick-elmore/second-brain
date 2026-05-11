using SecondBrain.Files.Models;

namespace SecondBrain.Files;

public sealed class SourceFolderScanner
{
    public IEnumerable<SourceFile> Scan(SourceFolder folder, int maxBytes)
    {
        if (!Directory.Exists(folder.AbsolutePath))
            yield break;

        foreach (var filePath in EnumerateFiles(folder.AbsolutePath, folder.ExcludeSubfolders))
        {
            if (HasExcludedExtension(filePath))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(filePath);
            }
            catch (Exception)
            {
                continue;
            }

            if (!info.Exists)
                continue;

            if (info.Length > maxBytes)
                continue;

            TryUnblock(filePath);

            var relative = Path.GetRelativePath(folder.AbsolutePath, filePath);
            var mtime = info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
            var ctime = info.CreationTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;

            yield return new SourceFile(
                SourceFolderId: folder.Id,
                AbsolutePath: filePath,
                RelativePath: relative,
                SizeBytes: info.Length,
                MTime: mtime,
                CTime: ctime);
        }
    }

    // Removes the Zone.Identifier alternate data stream that Windows stamps on files
    // transferred from another machine. LocalSystem cannot read MOTW-flagged files
    // even with full ACL access, so the scanner would silently skip them otherwise.
    private static void TryUnblock(string path)
    {
        try { File.Delete($"{path}:Zone.Identifier"); }
        catch { }
    }

    private static IEnumerable<string> EnumerateFiles(string root, IReadOnlySet<string> excludeSubfolders)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                var dirName = Path.GetFileName(entry);
                if (excludeSubfolders.Contains(dirName) || ScanDefaults.ExcludeSubfolders.Contains(dirName))
                    continue;

                foreach (var nested in EnumerateFiles(entry, excludeSubfolders))
                    yield return nested;
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static bool HasExcludedExtension(string filePath)
    {
        foreach (var ext in ScanDefaults.ExcludeFileExtensions)
        {
            if (filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
