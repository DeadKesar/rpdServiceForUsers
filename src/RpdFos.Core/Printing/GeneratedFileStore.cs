using Microsoft.Extensions.Options;

namespace RpdFos.Core.Printing;

/// <summary>Настройки формирования файлов — раздел <c>Generation</c> конфигурации.</summary>
public sealed class GenerationSettings
{
    /// <summary>Каталог сформированных файлов; относительный путь хост приводит к каталогу приложения.</summary>
    public string StoragePath { get; set; } = "storage/generated";

    /// <summary>Срок хранения сформированного файла, дней (FR-GEN-06).</summary>
    public int RetentionDays { get; set; } = 30;
}

/// <summary>
/// Сформированные файлы на диске (решение по хранению: невосстановимые файлы — в базе, сформированные — на томе
/// хранилища, их можно сформировать заново). Путь в базе — относительно каталога из настройки: каталог можно
/// перенести, не трогая записей.
/// </summary>
public sealed class GeneratedFileStore(IOptions<GenerationSettings> settings)
{
    private string Root => settings.Value.StoragePath;

    /// <summary>Сохраняет файл, возвращает путь относительно каталога хранилища.</summary>
    public async Task<string> SaveAsync(Guid id, string extension, byte[] content, DateTimeOffset now, CancellationToken ct = default)
    {
        var relative = Path.Combine(now.ToString("yyyy-MM"), $"{id}.{extension}");
        var full = Full(relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);
        return relative.Replace('\\', '/');
    }

    public bool Exists(string storagePath) => File.Exists(Full(storagePath));

    public Stream Open(string storagePath) => File.OpenRead(Full(storagePath));

    public void Delete(string storagePath)
    {
        var full = Full(storagePath);
        if (File.Exists(full)) File.Delete(full);
    }

    private string Full(string storagePath) => Path.GetFullPath(Path.Combine(Root, storagePath));
}
