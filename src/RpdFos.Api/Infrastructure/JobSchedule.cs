using Hangfire;
using RpdFos.Core.Printing;
using RpdFos.Core.Mail;
using RpdFos.Core.Reminders;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Повторяющиеся задачи хоста. Вынесены из <c>Program.cs</c>, чтобы расписание (час и пояс) проверялось тестом: сам хост
/// в тестах Hangfire не запускает.
/// </summary>
public static class JobSchedule
{
    public const string CleanupId = "generated-files-cleanup";
    public const string MailId = "notification-mail";
    public const string RemindersId = "rework-reminders";

    /// <param name="zone">Пояс сервера: в нём заданы <c>Reminders:Hour</c> и <c>Mail:DigestHour</c>.</param>
    public static void Register(IRecurringJobManager recurring, MailSettings mail, RemindersSettings reminders, TimeZoneInfo zone)
    {
        // Сформированные файлы с истёкшим сроком хранения удаляются раз в сутки (FR-GEN-06).
        recurring.AddOrUpdate<GeneratedFilesCleanup>(
            CleanupId, JobQueues.Generation, job => job.RunAsync(CancellationToken.None), Cron.Daily());

        // Письма о событиях уходят раз в минуту: «в пределах минуты после события» (этап 7, A4). Выключенная почта снимает
        // и расписание, оставшееся в хранилище от прежнего запуска, — иначе задача шла бы и без настройки.
        if (mail.Enabled)
            recurring.AddOrUpdate<NotificationMailJob>(
                MailId, JobQueues.Mail, job => job.RunAsync(CancellationToken.None), Cron.Minutely());
        else
            recurring.RemoveIfExists(MailId);

        // Напоминания о сроке доработки — раз в сутки в час Reminders:Hour по поясу сервера (этап 8, A5): раньше часа
        // дайджеста — это проверяет старт, — и письма «раз в день» о них уходят в то же утро. Очередь формирования — как у
        // очистки файлов: в ней исполнитель есть и при выключенной почте.
        if (reminders.Enabled)
            recurring.AddOrUpdate<ReworkReminders>(
                RemindersId, JobQueues.Generation, job => job.RunAsync(CancellationToken.None), Cron.Daily(reminders.Hour),
                new RecurringJobOptions { TimeZone = zone });
        else
            recurring.RemoveIfExists(RemindersId);
    }
}
