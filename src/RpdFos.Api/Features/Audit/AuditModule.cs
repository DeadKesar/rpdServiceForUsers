using Carter;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.Audit;

/// <summary>
/// Срез «Журнал аудита» (этап 8, A11; FR-ADM-02, S-53): лента событий системы с отбором по пользователю, периоду и типу —
/// только администратору. Только чтение; экспорт журнала — перспектива (FR-ADM-07). Период задаётся датами: границы суток —
/// по поясу сервера, как и час напоминаний и дайджеста.
/// </summary>
public class AuditModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/audit").WithTags("Audit").RequireAuthorization(Policies.Admin);
        g.MapGet("/", Page);
        g.MapGet("/filters", Filters);
    }

    private static async Task<IResult> Page(
        AuditJournal journal, TimeProvider clock, CancellationToken ct,
        Guid? userId = null, DateOnly? from = null, DateOnly? to = null, string? eventType = null,
        int skip = 0, int take = AuditJournal.DefaultPage)
    {
        if (from is { } start && to is { } end && end < start)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Период задан неверно",
                detail: "Дата «по» раньше даты «с». Поменяйте даты местами.");

        var zone = clock.LocalTimeZone;
        // Конец включительно: до начала следующих суток.
        var filter = new AuditFilter(userId, from is { } f ? Midnight(f, zone) : null, to is { } t ? Midnight(t.AddDays(1), zone) : null, eventType);
        return Results.Ok(await journal.PageAsync(filter, skip, take, ct));
    }

    private static async Task<IResult> Filters(AuditJournal journal, CancellationToken ct) => Results.Ok(await journal.OptionsAsync(ct));

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        // Несуществующая полночь (переход на летнее время) сдвигается на час вперёд: у пояса сервера такого перехода нет, но и падать нельзя.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        // Npgsql пишет в timestamptz только время с нулевым смещением.
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
