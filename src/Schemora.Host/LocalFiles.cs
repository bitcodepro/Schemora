namespace Schemora.Host;

public sealed class AppException(string code, string message, int status = 400, string? detail = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string? Detail { get; } = detail;
}

public static class LocalFiles
{
    public static string Validate(string? input, string extension)
    {
        if (string.IsNullOrWhiteSpace(input) || !Path.IsPathFullyQualified(input))
            throw new AppException("ABSOLUTE_PATH_REQUIRED", "Укажите абсолютный путь к локальному файлу.");
        var path = Path.GetFullPath(input);
        if (path.StartsWith(@"\\") || new Uri(path).IsUnc)
            throw new AppException("LOCAL_PATH_REQUIRED", "Сетевые пути не поддерживаются. Выберите локальный файл.");
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new AppException("FILE_TYPE_INVALID", $"Ожидается файл {extension}.");
        if (!File.Exists(path))
            throw new AppException("FILE_NOT_FOUND", "Файл не найден.", 404);
        CheckNoLinks(path);
        if (new FileInfo(path).Length > 128L * 1024 * 1024)
            throw new AppException("FILE_TOO_LARGE", "Поддерживаются файлы размером до 128 МБ.");
        return path;
    }

    public static void CheckNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AppException("SYMLINK_NOT_SUPPORTED", "Символические ссылки и junction не поддерживаются.");
            if (Path.GetPathRoot(current) == current)
                break;
        }
    }

    public static void Stage(string source, string target, CancellationToken cancellationToken)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            "bin",
            "obj",
            "node_modules",
            "dist",
            "work",
            ".tools",
        };
        var pending = new Stack<(string From, string To)>();
        pending.Push((source, target));
        long bytes = 0;
        var count = 0;
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(entry.To);
            foreach (var child in Directory.EnumerateFileSystemEntries(entry.From))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new AppException("SYMLINK_NOT_SUPPORTED", "Сборка отменена: каталог проекта содержит ссылку или junction.");
                var name = Path.GetFileName(child);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!excluded.Contains(name))
                        pending.Push((child, Path.Combine(entry.To, name)));
                    continue;
                }
                bytes += new FileInfo(child).Length;
                if (++count > 20_000 || bytes > 512L * 1024 * 1024)
                    throw new AppException("PROJECT_TOO_LARGE", "Служебная копия проекта превышает лимит: 20 000 файлов / 512 МБ.");
                File.Copy(child, Path.Combine(entry.To, name));
            }
        }
    }
}
