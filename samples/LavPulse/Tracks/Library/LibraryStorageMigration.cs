using System.Security.Cryptography;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Copies a closed library to another volume, verifying files before switching its root.</summary>
internal static class LibraryStorageMigration
{
    private const string MarkerName = ".lavpulse-transfer-source";
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Every selected drive/folder has an isolated LavPulse/library subdirectory.</summary>
    internal static string ResolveRoot(string defaultRoot, string? selectedDirectory) =>
        string.IsNullOrWhiteSpace(selectedDirectory) ? Path.GetFullPath(defaultRoot) :
            Path.Combine(Path.GetFullPath(selectedDirectory), "LavPulse", "library");

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), PathComparison);

    public static async Task MigrateAsync(string sourceRoot, string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (SamePath(source, destination)) return;

        string sourcePrefix = source + Path.DirectorySeparatorChar;
        string targetPrefix = destination + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, PathComparison) || source.StartsWith(targetPrefix, PathComparison))
            throw new InvalidOperationException("Каталог хранения нельзя вложить внутрь другой медиатеки.");

        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(source))
        {
            // An empty library on first launch is safe to relocate, but existing data must not
            // silently disappear if a removable disk was disconnected.
            if (Directory.Exists(destination))
                throw new DirectoryNotFoundException("Исходная медиатека недоступна. Подключи прежний диск.");
            Directory.CreateDirectory(destination);
            return;
        }

        string marker = Path.Combine(destination, MarkerName);
        if (Directory.Exists(destination))
        {
            if (File.Exists(marker))
            {
                if (!SamePath((await File.ReadAllTextAsync(marker, cancellationToken)).Trim(), source))
                    throw new IOException("В выбранной папке обнаружен незавершённый перенос другой медиатеки.");
            }
            else if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("В выбранной папке уже находятся данные. Выбери пустое расположение.");
        }

        // Never enumerate into reparse-point directories. Only regular files are migrated.
        var files = Directory.EnumerateFiles(source, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }).OrderBy(file => Path.GetFileName(file).Equals("queue.json", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
          .ToArray();

        // Reserve enough free space for full copy when moving between volumes. The source
        // remains available until every destination file has been verified.
        long bytes = files.Sum(file => new FileInfo(file).Length);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(destination)!);
            if (drive.IsReady && drive.AvailableFreeSpace < bytes + 1024 * 1024)
                throw new IOException("Недостаточно свободного места для переноса медиатеки.");
        }
        catch (ArgumentException) { }
        catch (DriveNotFoundException) { }

        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(marker, source, cancellationToken).ConfigureAwait(false);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && await FilesEqualAsync(file, target, cancellationToken).ConfigureAwait(false))
                continue;

            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!await FilesEqualAsync(file, temporary, cancellationToken).ConfigureAwait(false))
                    throw new IOException($"Не удалось проверить копию: {relative}");
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        // The source tree is intentionally not deleted here: the caller must first commit
        // the destination path to durable preferences.
        // The marker is removed only after the new active location has been committed.
    }

    internal static void Confirm(string destinationRoot)
    {
        string marker = Path.Combine(Path.GetFullPath(destinationRoot), MarkerName);
        if (File.Exists(marker)) File.Delete(marker);
    }

    private static async Task<bool> FilesEqualAsync(string source, string target, CancellationToken token)
    {
        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        if (sourceInfo.Length != targetInfo.Length) return false;
        await using var input = File.OpenRead(source);
        await using var output = File.OpenRead(target);
        byte[] original = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        byte[] copied = await SHA256.HashDataAsync(output, token).ConfigureAwait(false);
        return original.AsSpan().SequenceEqual(copied);
    }
}
