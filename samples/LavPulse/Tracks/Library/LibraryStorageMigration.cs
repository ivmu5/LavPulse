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

    internal static bool PathsOverlap(string a, string b)
    {
        string first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        string second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        string firstPrefix = Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar;
        string secondPrefix = Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar;
        return SamePath(first, second) || first.StartsWith(secondPrefix, PathComparison) ||
            second.StartsWith(firstPrefix, PathComparison);
    }

    public static async Task MigrateAsync(string sourceRoot, string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (SamePath(source, destination)) return;

        if (PathsOverlap(source, destination))
            throw new InvalidOperationException("Каталог хранения нельзя вложить внутрь другой медиатеки.");

        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Исходная медиатека недоступна: накопитель не подключён.");

        string marker = Path.Combine(destination, MarkerName);
        if (Directory.Exists(destination))
        {
            if (File.Exists(marker))
            {
                if (!SamePath((await File.ReadAllTextAsync(marker, cancellationToken)).Trim(), source))
                    throw new IOException("В выбранной папке обнаружен незавершённый перенос другой медиатеки.");
            }
            else if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("Для переноса требуется пустая папка.");
        }

        // Skipping a symlink/junction and then deleting the source tree can lose data.
        // Reject such trees before copying or writing any files.
        var files = ValidateRegularTree(source, cancellationToken)
            .OrderBy(file => Path.GetFileName(file).Equals("queue.json", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToArray();
        if (Directory.Exists(destination)) ValidateRegularTree(destination, cancellationToken);
        // Also reject a destination below an existing redirecting directory. Android's
        // system storage aliases are resolved by the platform; check custom Windows paths.
        if (OperatingSystem.IsWindows())
        {
            for (var parent = new DirectoryInfo(destination); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Путь назначения содержит символическую ссылку или junction.");
        }

        // Reserve enough free space for full copy when moving between volumes. The source
        // remains available until every destination file has been verified.
        // Resume may already contain most of the verified files. Count only missing
        // or mismatching copies, otherwise a nearly completed move can never resume.
        long bytes = 0;
        var verified = new HashSet<string>();
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (!File.Exists(target) || !await FilesEqualAsync(file, target, cancellationToken).ConfigureAwait(false))
                bytes += new FileInfo(file).Length;
            else verified.Add(file);
        }
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
            if (verified.Contains(file)) continue;

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
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Flush(flushToDisk: true);
                }
                if (!await FilesEqualAsync(file, temporary, cancellationToken).ConfigureAwait(false))
                    throw new IOException($"Не удалось проверить копию: {relative}");
                cancellationToken.ThrowIfCancellationRequested();
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

    internal static List<string> ValidateRegularTree(string root, CancellationToken token = default)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Перенос каталога с символическими ссылками запрещён.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
            {
                AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false
            }))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("В медиатеке найдена символическая ссылка. Перенос отменён.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else files.Add(entry);
            }
        }
        return files;
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
