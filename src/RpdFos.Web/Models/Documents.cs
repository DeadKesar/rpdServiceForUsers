using System.Text.Json;

namespace RpdFos.Web.Models;

// Контракт API документов (Features/Documents) на стороне клиента.

public sealed record DocumentListItem(Guid Id, string Title, string Type, string Status, string StatusTitle, bool IsOutdated,
    string DisciplineCode, string DisciplineName, PlanItem? Plan, int TemplateRevision, string AuthorName, DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt);

public sealed record DisciplineOption(Guid Id, string Code, string Name, Guid? DocumentId, string? DocumentAuthor, bool CanOpen);

public sealed record DocumentItem(Guid Id, string Type, string Title, string Status, string StatusTitle, bool IsOutdated, int Version,
    Guid TemplateId, int TemplateRevision, Guid DisciplineId, string DisciplineCode, string DisciplineName, Guid AuthorId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SubmittedAt);

/// <summary>Что пользователь может сделать с документом: отправить, проверить (вернуть, принять, замечания), разрешать замечания.</summary>
public sealed record DocumentActions(bool Submit, bool Review, bool ResolveComments, bool Print);

public sealed record DocumentDetails(DocumentItem Document, JsonElement Payload, JsonElement Schema, DocumentMetadata Metadata,
    bool Editable, DocumentActions Actions);

public sealed record DocumentMetadata(List<SectionState> Sections, List<string> ReadOnlyPaths, List<string> HiddenPaths,
    List<CompetenceCandidate> CompetenceCandidates);

public sealed record SectionState(string Key, string Title, bool Visible, string? StubText, string Fill, List<FieldError> Missing,
    List<FieldError> Warnings);

/// <summary>Замечание проверки: место, сообщение и уровень — ошибка блокирует отправку, предупреждение нет.</summary>
public sealed record FieldError(string Path, string Message, string Level = "error")
{
    public bool IsWarning => Level == "warning";
}

public sealed record CompetenceCandidate(Guid Id, string Code, string Formulation, string Category, List<IndicatorCandidate> Indicators);

public sealed record IndicatorCandidate(Guid Id, string Code, string Text, string Type);

public sealed record SectionSaved(int Version, string Status, string StatusTitle, SectionState Section, DateTimeOffset SavedAt);

public sealed record DocumentSaved(int Version, string Status, string StatusTitle, DateTimeOffset SavedAt);

/// <summary>Ответ об ошибке API документов: причины по полям — путь и сообщение.</summary>
public sealed record DocumentProblem(string? Title, string? Detail, List<FieldError>? Errors, Guid? DocumentId);

public static class SectionFills
{
    public const string Complete = "complete", Incomplete = "incomplete", NotApplicable = "not_applicable";
}

public static class DocumentTitles
{
    public static readonly (string Code, string Title)[] Statuses =
    [
        ("draft", "Черновик"), ("saved_draft", "Сохранённый черновик"), ("under_review", "На проверке"),
        ("returned", "На доработке"), ("accepted", "Принят"), ("approval", "На согласовании"), ("approved", "Согласован"),
        ("archived", "Архивный")
    ];
}

/// <summary>Статус сменился: новая версия и статус; при отправке — предупреждения полной проверки.</summary>
public sealed record DocumentReviewed(int Version, string Status, string StatusTitle, List<FieldError> Warnings);

/// <summary>Замечание методиста к полю: кто и когда оставил, разрешено ли и кем.</summary>
public sealed record CommentItem(Guid Id, string FieldPath, string Text, Guid AuthorId, string AuthorName, DateTimeOffset CreatedAt,
    bool IsResolved, string? ResolvedByName, DateTimeOffset? ResolvedAt);

/// <summary>Задача формирования печатной формы: статус, файл (если сформирован) и отчёт.</summary>
public sealed record GenerationItem(Guid TaskId, string Format, string Status, string? ErrorMessage, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, Guid? FileId, string? FileName, long? SizeBytes, DateTimeOffset? ExpiresAt, List<string> Warnings,
    List<string> Unfilled)
{
    public bool InProgress => Status is "queued" or "running";
}
