using System.Net;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Import;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Фоновые задачи Hangfire: очередь разбора выполняет импорт, панель — только администратору.</summary>
[Collection(DatabaseCollection.Name)]
public class JobsTests(DatabaseFixture app)
{
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(AppRoles.Teacher, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.Methodist, HttpStatusCode.Forbidden)]
    [InlineData(AppRoles.Admin, HttpStatusCode.OK)]
    public async Task Dashboard_is_for_admin_only(string? role, HttpStatusCode expected)
    {
        var response = await app.CreateClientFor(role).GetAsync("/hangfire");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Parsing_queue_runs_import_job()
    {
        Guid runId = default;
        await app.WithDbAsync(async db =>
        {
            var methodist = await ImportFiles.AddMethodistAsync(db);
            runId = (await ImportFiles.AddRunAsync(db, methodist.Id, ImportFiles.Read())).Run.Id;
        });

        var jobId = app.Services.GetRequiredService<IBackgroundJobClient>()
            .Enqueue<ImportJob>(JobQueues.Parsing, job => job.RunAsync(runId, CancellationToken.None));

        var run = await WaitForCompletionAsync(runId);
        var monitoring = app.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        var details = monitoring.JobDetails(jobId);
        var history = details?.History
            .Select(h => $"{h.StateName}: {h.Reason} {string.Join("; ", h.Data.Select(d => $"{d.Key}={d.Value}"))}");
        Assert.True(run.Status != ParserRunStatuses.Running,
            "Задача не выполнена: " + string.Join(" | ", history ?? []) +
            $" || job.Queue={details?.Job?.Queue}" +
            " || queues: " + string.Join(", ", monitoring.Queues().Select(q => $"{q.Name}={q.Length}")) +
            " || servers: " + string.Join(", ", monitoring.Servers().Select(s => $"{s.Name} [{string.Join("/", s.Queues)}] hb={s.Heartbeat:O}")));
        Assert.Equal(ParserRunStatuses.Partial, run.Status);
        Assert.Equal(46, run.ExtractedDisciplinesCount);
        Assert.NotNull(run.CreatedCurriculumPlanId);
    }

    private async Task<ParserRun> WaitForCompletionAsync(Guid runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            ParserRun? run = null;
            await app.WithDbAsync(async db => run = await db.ParserRuns.AsNoTracking().SingleAsync(r => r.Id == runId));
            if (run!.Status != ParserRunStatuses.Running || DateTime.UtcNow > deadline) return run;
            await Task.Delay(500);
        }
    }
}
