namespace RpdFos.Web.Models;

/// <summary>Запись журнала аудита (этап 8, A11). <c>DocumentId</c> — документ для ссылки, если он есть.</summary>
public sealed record AuditRecord(
    Guid Id, DateTimeOffset At, Guid? UserId, string? UserName, string EventType, string Title, string? EntityType, Guid? EntityId,
    string? EntityTitle, Guid? DocumentId, string? FromStatus, string? ToStatus, string? Comment, List<string> Details);

public sealed record AuditPage(List<AuditRecord> Items, int Total);

public sealed record AuditUserOption(Guid Id, string Name);

public sealed record AuditEventOption(string Code, string Title);

public sealed record AuditFilterOptions(List<AuditUserOption> Users, List<AuditEventOption> Events);
