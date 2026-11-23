namespace RpdFos.Web.Models;

/// <summary>Строка истории документа (этап 8, A10): переход статуса или событие аудита.</summary>
public sealed record HistoryEntry(
    DateTimeOffset At, string Kind, string Event, string Title, Guid? ById, string? ByName, string? FromStatus, string? ToStatus,
    string? Comment, List<string> Details);

public sealed record HistoryPage(List<HistoryEntry> Items, int Total);
