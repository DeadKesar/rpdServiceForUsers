using System.Text.Json;

namespace RpdFos.Web.Models;

// Контракт API документов (Features/Documents) на стороне клиента.

public sealed record DocumentListItem(Guid Id, string Title, string Type, string Status, string StatusTitle, bool IsOutdated,
    string DisciplineCode, string DisciplineName, PlanItem? Plan, int TemplateRevision, string AuthorName, DateTimeOffset UpdatedAt);

public sealed record DisciplineOption(Guid Id, string Code, string Name, Guid? DocumentId, string? DocumentAuthor, bool CanOpen);

public sealed record DocumentItem(Guid Id, string Type, string Title, string Status, string StatusTitle, bool IsOutdated, int Version,
    Guid TemplateId, int TemplateRevision, Guid DisciplineId, string DisciplineCode, string DisciplineName, Guid AuthorId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record DocumentDetails(DocumentItem Document, JsonElement Payload, JsonElement Schema, DocumentMetadata Metadata,
    bool Editable);

public sealed record DocumentMetadata(List<SectionState> Sections, List<string> ReadOnlyPaths, List<string> HiddenPaths,
    List<CompetenceCandidate> CompetenceCandidates);

public sealed record SectionState(string Key, string Title, bool Visible, string? StubText, string Fill, List<FieldError> Missing);

public sealed record FieldError(string Path, string Message);

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
