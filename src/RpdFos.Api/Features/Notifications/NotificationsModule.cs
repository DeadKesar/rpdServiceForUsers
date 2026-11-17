using System.Text.Json;
using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Directory;
using RpdFos.Core.Notifications;

namespace RpdFos.Api.Features.Notifications;

/// <summary>
/// Срез «Уведомления» (этап 6.1, C3; FR-REV-08): лента пользователя для колокольчика, число непрочитанных, отметки
/// «прочитано». Уведомления создаёт ядро (<c>DocumentNotifications</c>) в одном сохранении с событием; здесь — только
/// чтение своих. Чужое уведомление неотличимо от несуществующего: 404.
/// <para>
/// События, у которых пользователь выключил канал «в интерфейсе» (этап 7, A5), для ленты не существуют: их нет в списке и
/// в счётчике, а «прочитано» и «прочитать все» их не затрагивают — у скрытого события может ждать письмо, а прочитанное
/// письмом не отправляется. Скрывает текущая настройка, а не настройка на момент события: включённый обратно канал
/// возвращает события в ленту.
/// </para>
/// </summary>
public class NotificationsModule : ICarterModule
{
    public const int DefaultLimit = 20, MaxLimit = 100;

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();
        g.MapGet("/", Latest);
        g.MapGet("/unread-count", UnreadCount);
        g.MapPost("/read-all", ReadAll);
        g.MapPost("/{id:guid}/read", Read);
    }

    /// <summary>
    /// Последние уведомления — от новых к старым. Пользователь без записи справочника (учётная запись не связана)
    /// получателем быть не может, лента у него пуста: колокольчик есть у всех и не должен отвечать им ошибкой.
    /// </summary>
    private static async Task<IResult> Latest(
        int? limit, HttpContext http, AppDbContext db, DirectoryAccounts directory, NotificationSettings settings, CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return Results.Ok(Array.Empty<NotificationItem>());

        var hidden = await settings.HiddenInAppAsync(me, ct);
        var rows = await db.Notifications.AsNoTracking().Where(n => n.RecipientId == me && !hidden.Contains(n.EventType))
            .OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .Select(n => new { n.Id, n.EventType, n.DocumentId, n.Payload, n.CreatedAt, n.ReadAt })
            .ToListAsync(ct);
        return Results.Ok(rows.Select(n => new NotificationItem(n.Id, n.EventType, n.DocumentId, TextOf(n.Payload), n.CreatedAt, n.ReadAt))
            .ToList());
    }

    /// <summary>Число непрочитанных — для значка на колокольчике; частичный индекс таблицы обслуживает этот запрос.</summary>
    private static async Task<IResult> UnreadCount(
        HttpContext http, AppDbContext db, DirectoryAccounts directory, NotificationSettings settings, CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return Results.Ok(new UnreadCount(0));
        var hidden = await settings.HiddenInAppAsync(me, ct);
        return Results.Ok(new UnreadCount(
            await db.Notifications.CountAsync(n => n.RecipientId == me && n.ReadAt == null && !hidden.Contains(n.EventType), ct)));
    }

    /// <summary>«Прочитано» для одного. Повторная отметка время прочтения не меняет.</summary>
    private static async Task<IResult> Read(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, NotificationSettings settings, TimeProvider clock,
        CancellationToken ct)
    {
        var me = await directory.FindIdAsync(http.User, ct);
        var hidden = me is Guid recipient ? await settings.HiddenInAppAsync(recipient, ct) : [];
        var notification = me is null
            ? null
            : await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.RecipientId == me && !hidden.Contains(n.EventType), ct);
        if (notification is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Уведомление не найдено",
                detail: "Обновите список уведомлений.");

        notification.ReadAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>«Прочитать все»: одним запросом, уже прочитанные и скрытые в интерфейсе не затрагиваются.</summary>
    private static async Task<IResult> ReadAll(
        HttpContext http, AppDbContext db, DirectoryAccounts directory, NotificationSettings settings, TimeProvider clock,
        CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is Guid me)
        {
            var now = clock.GetUtcNow();
            var hidden = await settings.HiddenInAppAsync(me, ct);
            await db.Notifications.Where(n => n.RecipientId == me && n.ReadAt == null && !hidden.Contains(n.EventType))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        }
        return Results.NoContent();
    }

    // Готовая фраза лежит в payload.text; остальное в payload — параметры события для интерфейса.
    private static string TextOf(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }
}

/// <summary>Уведомление в ленте: что случилось (<c>EventType</c>), с каким документом, готовый текст, когда и прочитано ли.</summary>
public record NotificationItem(Guid Id, string EventType, Guid? DocumentId, string Text, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public record UnreadCount(int Unread);
