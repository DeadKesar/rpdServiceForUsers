using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Notifications;

namespace RpdFos.Core.Mail;

/// <summary>
/// Фоновая отправка уведомлений по почте (этап 7, A4, A5, FR-REV-08). Хост запускает её раз в минуту — письмо «сразу» уходит
/// в пределах минуты после события. Без зависимости от Hangfire: расписание и очередь задаёт хост.
/// <para>
/// Берутся уведомления, которые ещё не отправлены и не прочитаны (прочитанное в интерфейсе письмом не дублируется;
/// уведомление, которое система сама отметила прочитанным при замене согласующего, не пойдёт к тому, у кого документа
/// больше нет). Как отправлять, решает получатель в профиле (<see cref="NotificationSettings"/>), пока он ничего не менял —
/// умолчание события (<see cref="NotificationEvents.All"/>): «сразу» — своим письмом о каждом событии, «раз в день» —
/// одним дайджестом на получателя (<see cref="NotificationLetter.ComposeDigest"/>), «выключено» — письма нет. Адрес — email
/// учётной записи, связанной с записью справочника получателя: нет учётной записи, запись справочника неактивна или
/// учётную запись отключил администратор — письма нет.
/// </para>
/// <para>
/// У письма есть срок: «сразу» — время события, дайджест — ближайший час <see cref="MailSettings.DigestHour"/> после
/// события (<see cref="DigestSchedule"/>). Раньше срока письмо не уходит, позже чем через
/// <see cref="MailSettings.MaxAgeHours"/> после срока — тоже. В дайджест входят все уведомления получателя «раз в день»,
/// срок которых пришёл и ещё не вышел.
/// </para>
/// <para>
/// Событие и его уведомление сохранены раньше и от отправки не зависят: сбой оставляет <c>emailed_at</c> пустым, и
/// следующий запуск повторяет попытку. Все письма запуска идут по одному соединению (этап 8, A5). Время отправки пишется после каждого письма отдельно — при обрыве запуска
/// отправленное не повторится; у дайджеста оно пишется у всех его уведомлений разом. Недоступный сервер прерывает запуск,
/// неудача с одним письмом (сервер не принял адрес) — только это письмо.
/// </para>
/// </summary>
public sealed class NotificationMailJob(
    AppDbContext db, IMailSender sender, IOptions<MailSettings> settings, TimeProvider clock, ILogger<NotificationMailJob> log)
{
    // Дайджест ждёт своего часа не дольше суток (в сутки перехода на зимнее время — до 25 часов): ещё столько же, сверх
    // MaxAgeHours, уведомление остаётся подходящим кандидатом.
    private static readonly TimeSpan LongestDigestWait = TimeSpan.FromHours(25);

    /// <returns>Число отправленных писем: дайджест — одно письмо.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var options = settings.Value;
        var now = clock.GetUtcNow();
        var zone = clock.LocalTimeZone;
        var maxAge = TimeSpan.FromHours(options.MaxAgeHours);
        var since = now - maxAge - LongestDigestWait;
        // Почта включена по умолчанию у этих событий; у остальных она есть только у тех, чьи настройки сохранены в профиле.
        var mailedByDefault = NotificationEvents.All.Where(e => e.DefaultMail != MailMode.Off).Select(e => e.Type).ToList();

        var pending = await (
                from n in db.Notifications
                join e in db.Employees on n.RecipientId equals e.Id
                join u in db.Users on e.IdentitySubject equals u.Id.ToString()
                join p in db.NotificationPreferences on e.Id equals p.UserId into preferences
                from p in preferences.DefaultIfEmpty()
                where n.EmailedAt == null && n.ReadAt == null && n.CreatedAt >= since
                      && (mailedByDefault.Contains(n.EventType) || p != null)
                      && e.IsActive && e.CanLogin && u.Email != null
                orderby n.CreatedAt, n.Id
                select new { Notification = n, e.ShortName, u.Email, u.LockoutEnd, Preferences = p == null ? null : p.Preferences })
            .ToListAsync(ct);

        var letters = new List<Letter>();
        var digests = new Dictionary<Guid, (string Email, string ShortName, List<Notification> Notifications)>();
        var settingsOf = new Dictionary<Guid, Dictionary<string, EventSetting>>();

        // Бессрочная блокировка — учётную запись отключил администратор (этап 5.2): тот же признак, что у рассылки методистам.
        foreach (var row in pending.Where(r => r.LockoutEnd != DateTimeOffset.MaxValue))
        {
            var notification = row.Notification;
            if (!settingsOf.TryGetValue(notification.RecipientId, out var stored))
                settingsOf[notification.RecipientId] = stored = NotificationSettings.Parse(row.Preferences);

            var mode = NotificationSettings.Effective(notification.EventType, stored).Mail;
            if (mode == MailMode.Off) continue;
            var due = mode == MailMode.Daily ? DigestSchedule.NextAfter(notification.CreatedAt, options.DigestHour, zone) : notification.CreatedAt;
            if (due > now || now - due > maxAge) continue;

            if (mode == MailMode.Immediately)
            {
                letters.Add(new Letter([notification],
                    NotificationLetter.Compose(notification, row.Email!, row.ShortName, options.PublicUrl)));
                continue;
            }

            if (!digests.TryGetValue(notification.RecipientId, out var digest))
                digests[notification.RecipientId] = digest = (row.Email!, row.ShortName, []);
            digest.Notifications.Add(notification);
        }

        // Письма «сразу» — в порядке событий, дайджесты — после них, в порядке первого уведомления получателя.
        letters.AddRange(digests.Select(d => new Letter(d.Value.Notifications,
            NotificationLetter.ComposeDigest(d.Value.Notifications, d.Value.Email, d.Value.ShortName, options.PublicUrl, now, zone))));

        // Одно соединение на запуск (этап 8, A5): открывается перед первым письмом — без писем сервер не беспокоим.
        var sent = 0;
        IMailConnection? connection = null;
        try
        {
            foreach (var letter in letters)
            {
                try
                {
                    connection ??= await sender.OpenAsync(ct);
                    await connection.SendAsync(letter.Mail, ct);
                }
                catch (MailServerUnavailableException e)
                {
                    log.LogWarning(e, "Почтовый сервер недоступен: отправка повторится при следующем запуске");
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "Письмо по уведомлениям {NotificationIds} не отправлено: попытка повторится при следующем запуске",
                        string.Join(", ", letter.Notifications.Select(n => n.Id)));
                    continue;
                }

                var at = clock.GetUtcNow();
                foreach (var notification in letter.Notifications) notification.EmailedAt = at;
                await db.SaveChangesAsync(ct);
                sent++;
            }
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync();
        }

        if (sent > 0) log.LogInformation("Отправлено писем: {Count}", sent);
        return sent;
    }

    // Письмо и уведомления, которые оно закрывает: у письма «сразу» — одно, у дайджеста — все его уведомления.
    private sealed record Letter(IReadOnlyList<Notification> Notifications, OutgoingMail Mail);
}
