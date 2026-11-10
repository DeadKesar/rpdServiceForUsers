namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Строка электронного согласования (таблица document_signatures, раздел 2.4 ВКР): место листа согласования
/// шаблона, помеченное электронным, назначенный подписант и его отметка. Согласование параллельное — порядок
/// отметок не важен. В таблице — текущий круг: новый круг после возврата сбрасывает строки, а отметки прежнего
/// остаются в журнале аудита (этап 6, B1).
/// </summary>
public class DocumentSignature
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }

    /// <summary>Категория подписанта места из <c>signatures_config</c> — ключ строки в документе.</summary>
    public string Role { get; set; } = "";

    /// <summary>Правило, по которому искали подписанта.</summary>
    public string Resolver { get; set; } = "";

    /// <summary>Запись справочника: найдена по правилу или назначена вручную (FR-REV-13); null — не найдена.</summary>
    public Guid? ResolvedUserId { get; set; }

    public string Status { get; set; } = SignatureStatuses.Pending;

    /// <summary>Время отметки согласия. Время возврата — в истории статусов документа.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Причина возврата с согласования.</summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Новый круг: строка снова ждёт отметки, подписант — по правилу шаблона.</summary>
    public void Reset(string resolver, Guid? signatoryId, DateTimeOffset now) =>
        (Resolver, ResolvedUserId, Status, ApprovedAt, Comment, CreatedAt) = (resolver, signatoryId, SignatureStatuses.Pending, null, null, now);

    /// <summary>Назначить или заменить подписанта — пока строка ждёт отметки.</summary>
    public void Assign(Guid signatoryId)
    {
        Require(SignatureStatuses.Pending);
        ResolvedUserId = signatoryId;
    }

    public void Approve(DateTimeOffset now)
    {
        Require(SignatureStatuses.Pending);
        (Status, ApprovedAt) = (SignatureStatuses.Approved, now);
    }

    public void Reject(string comment)
    {
        Require(SignatureStatuses.Pending);
        (Status, Comment) = (SignatureStatuses.Rejected, comment);
    }

    // Вызывающий проверяет статус заранее и отвечает 409; здесь — защита от ошибки в коде.
    private void Require(string status)
    {
        if (Status != status)
            throw new InvalidOperationException($"Строка согласования «{Role}» уже отмечена: {SignatureStatuses.Title(Status)}.");
    }
}

public static class SignatureStatuses
{
    public const string Pending = "pending", Approved = "approved", Rejected = "rejected";

    public static readonly string[] All = [Pending, Approved, Rejected];

    public static string Title(string status) => status switch
    {
        Pending => "ожидает",
        Approved => "согласовано",
        Rejected => "возвращено",
        _ => status
    };
}
