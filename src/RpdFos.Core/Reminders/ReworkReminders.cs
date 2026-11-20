using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;

namespace RpdFos.Core.Reminders;

/// <summary>
/// Ежедневные напоминания о сроке доработки (этап 7, A6; FR-REV-09 и «приближение дедлайна» из FR-REV-08). Хост
/// запускает задачу раз в сутки; без зависимости от Hangfire: расписание и очередь задаёт хост.
/// <para>
/// Срок отсчитывается от последнего перехода документа в «На доработке» по истории статусов (<c>document_status_history</c>)
/// и равен <see cref="RemindersSettings.ReworkDays"/> суткам. За <see cref="RemindersSettings.WarnDaysBefore"/> суток до
/// срока автору уходит «Срок доработки истекает», по истечении срока заведующему кафедрой дисциплины и методистам —
/// «На доработке больше N дней». Каждое — один раз за одно пребывание на доработке: возврат документа начинает отсчёт
/// заново и снова разрешает оба напоминания. Если срок вышел, а предупреждения не было (сервер не работал, задачу
/// включили позже), автору о нём уже не сообщается: «истекает» о прошедшем сроке было бы неправдой.
/// </para>
/// <para>
/// Что напоминание уже было, определяется по самим уведомлениям — отдельного признака в таблицах нет: уведомление того же
/// события, созданное после начала нынешнего пребывания на доработке, по тому же документу. Документ — из
/// <c>payload.document_id</c>: у уведомления без ссылки <c>notifications.document_id</c> пуст. Событие одно на всех
/// получателей: методист, появившийся после эскалации, её уже не получит.
/// </para>
/// <para>
/// Заведующего кафедрой находит то же правило, что и лист согласования, — <c>discipline.department.head</c> (с
/// исполняющим обязанности, если заведующего нет). Не найден, не имеет действующей учётной записи (прочитать уведомление
/// он не сможет) — эскалация идёт только методистам. Ссылку на документ получает тот, кто его откроет — автор, методист,
/// администратор, согласующий со строкой в документе или закреплённый за дисциплиной (A10); заведующий без этого получает
/// уведомление без ссылки,
/// как прежний согласующий при замене (A3): переход по ней дал бы 403.
/// </para>
/// Напоминания идут через настройки пользователя (A5) и почту (A4): обычные уведомления с событиями
/// <see cref="NotificationEvents.ReworkDeadlineNear"/> и <see cref="NotificationEvents.ReworkOverdue"/>.
/// </summary>
public sealed class ReworkReminders(
    AppDbContext db, DocumentNotifications notifications, SignatoryResolution signatories, DirectoryAccounts directory,
    DocumentApproval approval, DisciplineAssignments assignments, IOptions<RemindersSettings> settings, TimeProvider clock,
    ILogger<ReworkReminders> log)
{
    private static readonly string[] ReminderEvents = [NotificationEvents.ReworkDeadlineNear, NotificationEvents.ReworkOverdue];

    /// <returns>Число созданных уведомлений (по одному на получателя).</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var options = settings.Value;
        var now = clock.GetUtcNow();
        var zone = clock.LocalTimeZone;
        var term = TimeSpan.FromDays(options.ReworkDays);
        var warnFrom = now - TimeSpan.FromDays(options.ReworkDays - options.WarnDaysBefore);

        // Документы на доработке, которым подошёл срок предупреждения, — с началом нынешнего пребывания на доработке.
        var stays = await db.Documents.AsNoTracking()
            .Where(d => d.Status == DocumentStatuses.Returned)
            .Select(d => new
            {
                Document = d,
                Since = db.DocumentStatusHistory.Where(h => h.DocumentId == d.Id && h.ToStatus == DocumentStatuses.Returned)
                    .Max(h => (DateTimeOffset?)h.ChangedAt)
            })
            .Where(s => s.Since <= warnFrom)
            .OrderBy(s => s.Since)
            .ToListAsync(ct);
        if (stays.Count == 0) return 0;

        var earliest = stays.Min(s => s.Since!.Value);
        var reminded = (await db.Notifications.AsNoTracking()
                .Where(n => ReminderEvents.Contains(n.EventType) && n.CreatedAt >= earliest)
                .Select(n => new { n.EventType, n.CreatedAt, n.Payload })
                .ToListAsync(ct))
            .Select(n => (n.EventType, n.CreatedAt, DocumentId: DocumentOf(n.Payload)))
            .ToList();

        // Напоминание этого пребывания: созданное не раньше его начала (более раннее — от прежнего возврата документа).
        bool Reminded(Guid documentId, string eventType, DateTimeOffset since) =>
            reminded.Any(n => n.EventType == eventType && n.DocumentId == documentId && n.CreatedAt >= since);

        IReadOnlyList<Guid>? methodists = null;
        foreach (var stay in stays)
        {
            var (document, since) = (stay.Document, stay.Since!.Value);
            if (now - since < term)
            {
                if (!Reminded(document.Id, NotificationEvents.ReworkDeadlineNear, since))
                    await notifications.ReworkDeadlineNearAsync(document, since, since + term, zone, now, ct);
                continue;
            }

            if (Reminded(document.Id, NotificationEvents.ReworkOverdue, since)) continue;
            methodists ??= await directory.ActiveHoldersOfAsync(AppRoles.Methodist, ct);
            await EscalateAsync(document, since, methodists, zone, now, ct);
        }

        var created = db.ChangeTracker.Entries<Notification>().Count(e => e.State == EntityState.Added);
        await db.SaveChangesAsync(ct);
        if (created > 0) log.LogInformation("Напоминания о сроке доработки: создано уведомлений {Count}", created);
        return created;
    }

    // Методисты видят любой документ — им ссылка. Заведующему — если он действующий пользователь и откроет документ.
    private async Task EscalateAsync(
        Document document, DateTimeOffset since, IReadOnlyList<Guid> methodists, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var linked = methodists.ToList();
        var unlinked = new List<Guid>();
        if (await signatories.FindAsync(DepartmentHeadResolver.Rule, document.DisciplineId, ct) is { } head
            && !linked.Contains(head.Id)
            && await directory.RolesOfActiveAsync(head.Id, ct) is { } roles)
            (await DocumentVisibility.CanOpenAsync(approval, assignments, document, head.Id, roles, ct) ? linked : unlinked).Add(head.Id);

        await notifications.ReworkOverdueAsync(document, settings.Value.ReworkDays, since, linked, unlinked, zone, now, ct);
    }

    private static Guid? DocumentOf(string payload) =>
        JsonNode.Parse(payload)?["document_id"]?.GetValue<string>() is { } text && Guid.TryParse(text, out var id) ? id : null;
}
