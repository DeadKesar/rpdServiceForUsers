namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Задача формирования файла документа (таблица generation_tasks). Отчёт о формировании — дополнение к проекту:
/// предупреждения (не найден подписант) и незаполненные места показываются пользователю вместе с файлом.
/// </summary>
public class GenerationTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string Format { get; set; } = GenerationFormats.Docx;
    public string Status { get; set; } = GenerationStatuses.Queued;
    public Guid RequestedBy { get; set; }
    public string? HangfireJobId { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Отчёт о формировании (jsonb): <c>{ "warnings": [...], "unfilled": [...] }</c>.</summary>
    public string? Report { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Сформированный файл (таблица generated_files): лежит на диске по пути из настройки, удаляется по сроку хранения
/// (FR-GEN-06). Имя для скачивания — по шаблону «Тип_Дисциплина_Год» (FR-GEN-03), дополнение к проекту.
/// </summary>
public class GeneratedFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GenerationTaskId { get; set; }

    /// <summary>Путь относительно каталога сформированных файлов.</summary>
    public string StoragePath { get; set; } = "";

    public string FileName { get; set; } = "";
    public string Format { get; set; } = GenerationFormats.Docx;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public static class GenerationFormats
{
    public const string Docx = "docx", Pdf = "pdf";

    public static readonly string[] All = [Docx, Pdf];
}

public static class GenerationStatuses
{
    public const string Queued = "queued", Running = "running", Succeeded = "succeeded", Failed = "failed";

    public static readonly string[] All = [Queued, Running, Succeeded, Failed];
}
