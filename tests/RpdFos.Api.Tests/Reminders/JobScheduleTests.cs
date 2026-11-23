using Hangfire;
using Hangfire.Common;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Mail;
using RpdFos.Core.Reminders;
using Xunit;

namespace RpdFos.Api.Tests.Reminders;

/// <summary>
/// Расписание повторяющихся задач хоста (этап 8, A5): напоминания идут в час <c>Reminders:Hour</c> по поясу сервера, письма —
/// раз в минуту; выключенная задача снимается из хранилища. Сам Hangfire в тестах не запускается — менеджер подменён.
/// </summary>
public class JobScheduleTests
{
    // Московское время: на стенде пояс московский (B5); пояс сборки тестов от него не зависит.
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone("msk", TimeSpan.FromHours(3), "msk", "msk");

    [Theory]
    [InlineData(7, "0 7 * * *")]
    [InlineData(0, "0 0 * * *")]
    [InlineData(5, "0 5 * * *")]
    public void The_reminders_run_every_day_at_the_hour_of_the_setting_in_the_zone_of_the_server(int hour, string cron)
    {
        var jobs = new RecordingJobs();

        JobSchedule.Register(jobs, new MailSettings(), new RemindersSettings { Hour = hour }, Moscow);

        var reminders = jobs.Added[JobSchedule.RemindersId];
        Assert.Equal((cron, Moscow), (reminders.Cron, reminders.Options.TimeZone));
        Assert.Equal(nameof(ReworkReminders.RunAsync), reminders.Job.Method.Name);
        Assert.Equal(typeof(ReworkReminders), reminders.Job.Type);
        Assert.Equal(JobQueues.Generation, reminders.Job.Queue); // очередь не потерялась из-за перегрузки с поясом
    }

    [Fact]
    public void The_mail_job_runs_every_minute_and_the_cleanup_every_day()
    {
        var jobs = new RecordingJobs();

        JobSchedule.Register(jobs, new MailSettings(), new RemindersSettings(), Moscow);

        Assert.Equal("* * * * *", jobs.Added[JobSchedule.MailId].Cron);
        Assert.Equal("0 0 * * *", jobs.Added[JobSchedule.CleanupId].Cron);
        Assert.Empty(jobs.Removed);
    }

    [Fact]
    public void A_disabled_job_is_removed_from_the_storage_instead_of_running_on_the_old_schedule()
    {
        var jobs = new RecordingJobs();

        JobSchedule.Register(jobs, new MailSettings { Enabled = false }, new RemindersSettings { Enabled = false }, Moscow);

        Assert.Equal([JobSchedule.CleanupId], jobs.Added.Keys);
        Assert.Equal([JobSchedule.MailId, JobSchedule.RemindersId], jobs.Removed.Order());
    }

    private sealed class RecordingJobs : IRecurringJobManager
    {
        public Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)> Added { get; } = [];

        public List<string> Removed { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Added[recurringJobId] = (job, cronExpression, options);

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => Removed.Add(recurringJobId);
    }
}
