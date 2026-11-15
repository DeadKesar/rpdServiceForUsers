namespace RpdFos.Web.Models;

// Контракт API уведомлений (Features/Notifications) на стороне клиента.

/// <summary>
/// Уведомление в ленте: что случилось (<c>EventType</c>), с каким документом, готовая фраза сервера, когда создано
/// и когда прочитано (<c>null</c> — не прочитано).
/// </summary>
public sealed record NotificationItem(Guid Id, string EventType, Guid? DocumentId, string Text, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>Число непрочитанных уведомлений — для значка на колокольчике.</summary>
public sealed record UnreadCount(int Unread);
