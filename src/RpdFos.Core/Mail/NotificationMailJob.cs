using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Mail;

/// <summary>
/// Фоновая отправка уведомлений по почте (этап 7, A4, FR-REV-08). Хост запускает её раз в минуту — письмо уходит в пределах
/// минуты после события. Без зависимости от Hangfire: расписание и очередь задаёт хост.
/// <para>
/// Берутся уведомления, которые ещё не отправлены и не прочитаны (прочитанное в интерфейсе письмом не дублируется;
/// уведомление, которое система сама отметила прочитанным при замене согласующего, не пойдёт к тому, у кого документа
/// больше нет), не старше <see cref="MailSettings.MaxAgeHours"/>, события которых по умолчанию уходят «сразу»
/// (<see cref="NotificationEvents.All"/>). Адрес — email учётной записи, связанной с записью справочника получателя:
/// нет учётной записи, запись справочника неактивна или учётную запись отключил администратор — письма нет.
/// </para>
/// <para>
/// Событие и его уведомление сохранены раньше и от отправки не зависят: сбой оставляет <c>emailed_at</c> пустым, и
/// следующий запуск повторяет попытку. Время отправки пишется после каждого письма отдельно — при обрыве запуска
/// отправленное не повторится. Недоступный сервер прерывает запуск, неудача с одним письмом (сервер не принял адрес) —
/// только это письмо.
/// </para>
/// </summary>
public sealed class NotificationMailJob(
    AppDbContext db, IMailSender sender, IOptions<MailSettings> settings, TimeProvider clock, ILogger<NotificationMailJob> log)
{
    /// <returns>Число отправленных писем.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var options = settings.Value;
        var since = clock.GetUtcNow() - TimeSpan.FromHours(options.MaxAgeHours);
        var immediate = NotificationEvents.All.Where(e => e.DefaultMail == MailMode.Immediately).Select(e => e.Type).ToList();

        var pending = await (
                from n in db.Notifications
                join e in db.Employees on n.RecipientId equals e.Id
                join u in db.Users on e.IdentitySubject equals u.Id.ToString()
                where n.EmailedAt == null && n.ReadAt == null && n.CreatedAt >= since && immediate.Contains(n.EventType)
                      && e.IsActive && e.CanLogin && u.Email != null
                orderby n.CreatedAt, n.Id
                select new { Notification = n, e.ShortName, u.Email, u.LockoutEnd })
            .ToListAsync(ct);

        var sent = 0;
        // Бессрочная блокировка — учётную запись отключил администратор (этап 5.2): тот же признак, что у рассылки методистам.
        foreach (var row in pending.Where(r => r.LockoutEnd != DateTimeOffset.MaxValue))
        {
            try
            {
                await sender.SendAsync(NotificationLetter.Compose(row.Notification, row.Email!, row.ShortName, options.PublicUrl), ct);
            }
            catch (MailServerUnavailableException e)
            {
                log.LogWarning(e, "Почтовый сервер недоступен: отправка повторится при следующем запуске");
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Письмо по уведомлению {NotificationId} не отправлено: попытка повторится при следующем запуске",
                    row.Notification.Id);
                continue;
            }

            row.Notification.EmailedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            sent++;
        }

        if (sent > 0) log.LogInformation("Отправлено писем: {Count}", sent);
        return sent;
    }
}
