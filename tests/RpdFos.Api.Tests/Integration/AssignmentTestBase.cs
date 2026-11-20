using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Assignments;
using RpdFos.Api.Features.Notifications;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Основа тестов закрепления дисциплин (этап 7, A10). Закрепления общие на всю базу коллекции, а дисциплина с закреплениями
/// меняет создание документов у всех: до и после каждого теста таблица очищается — тесты других классов видят дисциплины без
/// закреплений, как до A10. Тесты берут дисциплины реального плана 09.04.04 по индексу.
/// </summary>
public abstract class AssignmentTestBase(DatabaseFixture fixture) : IAsyncLifetime
{
    protected DatabaseFixture App { get; } = fixture;

    public Task InitializeAsync() => ClearAsync();

    public Task DisposeAsync() => ClearAsync();

    private Task ClearAsync() => App.WithDbAsync(async db => await db.DisciplineAssignments.ExecuteDeleteAsync());

    protected async Task<Guid[]> DisciplinesAsync(params string[] codes)
    {
        var ids = new List<Guid>();
        foreach (var code in codes) ids.Add(await DocumentData.DisciplineAsync(App, code));
        return [.. ids];
    }

    protected static Task<HttpResponseMessage> AssignAsync(HttpClient client, Employee employee, params Guid[] disciplines) =>
        client.PostAsJsonAsync("/api/discipline-assignments", new AssignRequest(employee.Id, [.. disciplines]));

    /// <summary>«Закрепить», который должен пройти: итог — число добавленных и уже бывших закреплений.</summary>
    protected static async Task<AssignmentSummary> AssignedAsync(HttpClient client, Employee employee, params Guid[] disciplines)
    {
        var response = await AssignAsync(client, employee, disciplines);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AssignmentSummary>())!;
    }

    protected async Task<AssignmentPage> PageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<AssignmentPage>($"/api/discipline-assignments?planId={await App.RealPlanIdAsync()}"))!;

    /// <summary>Строка страницы закрепления по индексу дисциплины.</summary>
    protected static DisciplineRow RowOf(AssignmentPage page, string code) => page.Disciplines.Single(d => d.Code == code);

    /// <summary>Закрепление сотрудника за дисциплиной — по странице: нужен идентификатор, чтобы его снять.</summary>
    protected static AssigneeItem AssigneeOf(AssignmentPage page, string code, Employee employee) =>
        RowOf(page, code).Assigned.Single(a => a.EmployeeId == employee.Id);

    /// <summary>Уведомления события из ленты пользователя — от новых к старым; у закрепления документа в них нет.</summary>
    protected static async Task<List<NotificationItem>> FeedAsync(HttpClient client, string eventType) =>
        (await client.GetFromJsonAsync<List<NotificationItem>>($"/api/notifications?limit={NotificationsModule.MaxLimit}"))!
            .Where(n => n.EventType == eventType).ToList();

    /// <summary>«09.04.04, набор 2023, версия 1» — как в тексте уведомления.</summary>
    protected async Task<string> PlanTitleAsync()
    {
        var planId = await App.RealPlanIdAsync();
        var title = "";
        await App.WithDbAsync(async db => title = await (
                from p in db.CurriculumPlans
                join s in db.StudyDirections on p.DirectionId equals s.Id
                where p.Id == planId
                select $"{s.Code}, набор {p.Year}, версия {p.Version}")
            .SingleAsync());
        return title;
    }

    /// <summary>«Б1.О.03 «Название»» — как дисциплина названа в тексте уведомления.</summary>
    protected async Task<string> NamedAsync(string code)
    {
        var id = await DocumentData.DisciplineAsync(App, code);
        var name = "";
        await App.WithDbAsync(async db => name = await db.Disciplines.Where(d => d.Id == id).Select(d => d.Name).SingleAsync());
        return $"{code} «{name}»";
    }

    protected static async Task<string?> ProblemTitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())?.Title;

    /// <summary>Запрос должен ответить кодом <paramref name="status"/>; иначе в сообщении — код и тело ответа.</summary>
    protected static async Task ExpectStatusAsync(HttpStatusCode status, Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(status == response.StatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
}
