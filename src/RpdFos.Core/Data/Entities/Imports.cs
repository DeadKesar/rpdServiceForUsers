namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Загруженный файл (таблица uploaded_files, контекст Infrastructure). Исходные данные невосстановимы,
/// поэтому содержимое хранится в базе (<see cref="Content"/>) и попадает в резервные копии.
/// </summary>
public class UploadedFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UploadedBy { get; set; }
    public string FileType { get; set; } = UploadedFileTypes.Other;
    public string OriginalName { get; set; } = "";
    public string? StoragePath { get; set; }
    public byte[]? Content { get; set; }
    public string MimeType { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 содержимого (hex) — для поиска дубликатов.</summary>
    public string Checksum { get; set; } = "";

    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>Запуск импорта исходных файлов (таблица parser_runs): статус, счётчики, замечания разбора.</summary>
public class ParserRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StartedBy { get; set; }
    public Guid PlanFileId { get; set; }
    public Guid CompListFileId { get; set; }
    public Guid CompMatrixFileId { get; set; }
    public Guid? EmployeesFileId { get; set; }
    public string Status { get; set; } = ParserRunStatuses.Running;
    public int ExtractedDisciplinesCount { get; set; }
    public int ExtractedCompetencesCount { get; set; }
    public int ExtractedEmployeesCount { get; set; }

    /// <summary>Замечания: JSON-массив объектов {severity, file, sheet, row, message}.</summary>
    public string ErrorsLog { get; set; } = "[]";

    public Guid? CreatedCurriculumPlanId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public static class UploadedFileTypes
{
    public const string CurriculumPlan = "curriculum_plan", CompetenceMatrix = "competence_matrix",
        CompetenceReference = "competence_reference", Employees = "employees", Other = "other";
}

public static class ParserRunStatuses
{
    public const string Running = "running", Succeeded = "succeeded", Partial = "partial", Failed = "failed";
}
