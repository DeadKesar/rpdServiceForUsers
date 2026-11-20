using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Assignments;
using RpdFos.Core.Data.Entities;
using Xunit;
using static RpdFos.Api.Tests.Integration.DocumentCircles;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Закрепление дисциплин (этап 7, A10; решение 5): методист и администратор закрепляют сотрудников за дисциплинами версии
/// плана на странице «Закрепление дисциплин». Учётные записи — настоящие, через страницу администратора: закрепить можно
/// запись с действующей учётной записью преподавателя, а уведомления и роли смотрят в базу. Как закрепление действует на
/// доступ и создание документов — в <see cref="DisciplineAccessApiTests"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DisciplineAssignmentApiTests(DatabaseFixture fixture) : AssignmentTestBase(fixture)
{
    private const string Module = "Б1.О.14", ElectiveGroup = "Б1.В.ДВ.01", Practice = "Б2.О.01(У)", Gia = "Б3.01(Д)";

    [Fact]
    public async Task Methodist_and_admin_assign_an_employee_to_several_disciplines_and_the_page_shows_them_with_the_documents()
    {
        var (methodist, methodistRecord) = await AccountAsync(App, AppRoles.Methodist);
        var (admin, adminRecord) = await AccountAsync(App, AppRoles.Admin);
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var (author, authorRecord) = await AccountAsync(App, AppRoles.Teacher, "Автор Документа Обезличенный");
        var created = await DocumentData.CreateAsync(App, author, (await DocumentData.PublishedTemplateAsync(App)).Id, "Б1.О.05");
        var ids = await DisciplinesAsync("Б1.О.03", "Б1.О.05", "Б1.О.08", "Б1.О.09");

        // Три дисциплины сразу; затем две прежние и одна новая: повтор ошибкой не считается — его число в ответе.
        Assert.Equal(new AssignmentSummary(3, 0), await AssignedAsync(methodist, holderRecord, ids[0], ids[1], ids[2]));
        Assert.Equal(new AssignmentSummary(1, 2), await AssignedAsync(admin, holderRecord, ids[1], ids[2], ids[3]));
        Assert.Equal(new AssignmentSummary(0, 4), await AssignedAsync(admin, holderRecord, ids));

        var page = await PageAsync(methodist);
        // Строки без документов — модуль, группа элективов, практика, ГИА — на странице не показаны (FR-DOC-18).
        Assert.DoesNotContain(page.Disciplines, d => d.Code is Module or ElectiveGroup or Practice or Gia);

        // Закреплённый, кто закрепил и документы дисциплины с автором и статусом.
        var row = RowOf(page, "Б1.О.05");
        var assignee = Assert.Single(row.Assigned);
        Assert.Equal((holderRecord.Id, holderRecord.ShortName, methodistRecord.ShortName), (assignee.EmployeeId, assignee.Name, assignee.AssignedBy));
        Assert.Equal(adminRecord.ShortName, AssigneeOf(page, "Б1.О.09", holderRecord).AssignedBy);
        var document = Assert.Single(row.Documents, d => d.Id == created.Document.Id);
        Assert.Equal((TemplateTypes.Rpd, S.Draft, authorRecord.ShortName), (document.Type, document.Status, document.AuthorName));
        Assert.Empty(RowOf(page, "Б1.О.10").Assigned);

        // Кафедра — коротким названием, а полный список кафедр плана — для фильтра страницы.
        await App.WithDbAsync(async db =>
        {
            var departmentId = await db.Disciplines.Where(d => d.Id == ids[1]).Select(d => d.DepartmentId).SingleAsync();
            var department = await db.Departments.SingleAsync(d => d.Id == departmentId);
            Assert.Equal((department.Id, department.ShortName), (row.DepartmentId, row.Department));
            Assert.Contains(page.Departments, d => (d.Id, d.Name, d.ShortName) == (department.Id, department.Name, department.ShortName));

            // Каждое закрепление — запись в журнале аудита (NFR-SEC-07): кто, какая дисциплина, за кем.
            var audit = await db.AuditLog.Where(a => a.EventType == AuditEvents.DisciplineAssigned &&
                (a.UserId == methodistRecord.Id || a.UserId == adminRecord.Id)).ToListAsync();
            Assert.Equal(ids.Order(), audit.Select(a => a.EntityId!.Value).Order());
            Assert.All(audit, a =>
            {
                Assert.Equal(AuditEntities.Discipline, a.EntityType);
                Assert.Equal(holderRecord.Id, JsonDocument.Parse(a.Payload).RootElement.GetProperty("employee").GetGuid());
            });
            Assert.Equal(adminRecord.Id, audit.Single(a => a.EntityId == ids[3]).UserId);
            Assert.Equal(methodistRecord.Id, audit.Single(a => a.EntityId == ids[0]).UserId);
        });

        // Закреплённому — одно уведомление на действие, а не на дисциплину; о повторе, который ничего не добавил, — ни одного.
        // Ссылки на документ нет: документа по дисциплине может ещё не быть. Закрепившим — ничего.
        var plan = await PlanTitleAsync();
        var feed = await FeedAsync(holder, NotificationEvents.DisciplineAssigned);
        Assert.Equal(2, feed.Count);
        Assert.Equal($"Вам закреплена дисциплина. {await NamedAsync("Б1.О.09")}. Учебный план: {plan}. Администратор: {adminRecord.ShortName}", feed[0].Text);
        Assert.Equal($"Вам закреплены дисциплины (3). {await NamedAsync("Б1.О.03")}; {await NamedAsync("Б1.О.05")}; {await NamedAsync("Б1.О.08")}. " +
                     $"Учебный план: {plan}. Методист: {methodistRecord.ShortName}", feed[1].Text);
        Assert.All(feed, n => Assert.Null(n.DocumentId));
        var parameters = await PayloadAsync(App, feed[1]);
        Assert.Equal((methodistRecord.ShortName, plan, 3),
            (parameters.GetProperty("by").GetString(), parameters.GetProperty("plan").GetString(), parameters.GetProperty("disciplines").GetArrayLength()));
        Assert.Empty(await FeedAsync(methodist, NotificationEvents.DisciplineAssigned));
        Assert.Empty(await FeedAsync(admin, NotificationEvents.DisciplineAssigned));
    }

    [Fact]
    public async Task Many_disciplines_are_named_by_the_first_three_and_a_number()
    {
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var (holder, holderRecord) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var ids = await DisciplinesAsync("Б1.О.01", "Б1.О.02", "Б1.О.03", "Б1.О.04", "Б1.О.05");

        await AssignedAsync(methodist, holderRecord, ids);

        var text = Assert.Single(await FeedAsync(holder, NotificationEvents.DisciplineAssigned)).Text;
        Assert.StartsWith($"Вам закреплены дисциплины (5). {await NamedAsync("Б1.О.01")}; {await NamedAsync("Б1.О.02")}; {await NamedAsync("Б1.О.03")} и ещё 2. ", text);
        Assert.DoesNotContain("Б1.О.04", text);
    }

    [Fact]
    public async Task Unassigning_removes_the_assignment_writes_the_audit_and_notifies_the_employee_unless_they_have_left()
    {
        var (methodist, methodistRecord) = await AccountAsync(App, AppRoles.Methodist);
        var (admin, _) = await AccountAsync(App, AppRoles.Admin);
        var (first, firstRecord) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var (_, leaverRecord, leaverAccount) = await AccountAsync(App, [AppRoles.Teacher], "Ушедший Сотрудник Обезличенный");
        var ids = await DisciplinesAsync("Б1.О.03", "Б1.О.05");
        await AssignedAsync(methodist, firstRecord, ids);
        await AssignedAsync(methodist, leaverRecord, ids[0]);

        // Снимается закрепление, а не человек: у первой дисциплины остаётся другой закреплённый, у второй — тот же.
        var removed = AssigneeOf(await PageAsync(methodist), "Б1.О.03", firstRecord);
        await ExpectStatusAsync(HttpStatusCode.NoContent, methodist.DeleteAsync($"/api/discipline-assignments/{removed.Id}"));
        var page = await PageAsync(methodist);
        Assert.Equal([leaverRecord.Id], RowOf(page, "Б1.О.03").Assigned.Select(a => a.EmployeeId));
        Assert.Equal([firstRecord.Id], RowOf(page, "Б1.О.05").Assigned.Select(a => a.EmployeeId));

        await App.WithDbAsync(async db =>
        {
            var audit = await db.AuditLog.SingleAsync(a => a.EventType == AuditEvents.DisciplineUnassigned && a.UserId == methodistRecord.Id);
            Assert.Equal((AuditEntities.Discipline, ids[0]), (audit.EntityType, audit.EntityId));
            Assert.Equal(firstRecord.Id, JsonDocument.Parse(audit.Payload).RootElement.GetProperty("employee").GetGuid());
        });
        var gone = Assert.Single(await FeedAsync(first, NotificationEvents.DisciplineUnassigned));
        Assert.Null(gone.DocumentId);
        Assert.Equal($"Закрепление дисциплины снято. {await NamedAsync("Б1.О.03")}. Учебный план: {await PlanTitleAsync()}. Методист: {methodistRecord.ShortName}", gone.Text);

        // Второй раз то же закрепление уже снято — 404.
        var again = await methodist.DeleteAsync($"/api/discipline-assignments/{removed.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("Закрепление не найдено", await ProblemTitleAsync(again));
        Assert.Equal(HttpStatusCode.NotFound, (await methodist.DeleteAsync($"/api/discipline-assignments/{Guid.NewGuid()}")).StatusCode);

        // Ушедшему сотруднику (учётную запись отключили) уведомление некому читать: закрепление снимается, уведомления нет.
        Assert.True((await admin.PostAsync($"/api/admin/users/{leaverAccount}/deactivate", null)).IsSuccessStatusCode);
        await ExpectStatusAsync(HttpStatusCode.NoContent,
            methodist.DeleteAsync($"/api/discipline-assignments/{AssigneeOf(await PageAsync(methodist), "Б1.О.03", leaverRecord).Id}"));
        Assert.Empty(RowOf(await PageAsync(methodist), "Б1.О.03").Assigned);
        await App.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientId == leaverRecord.Id && n.EventType == NotificationEvents.DisciplineUnassigned));
            Assert.Equal(2, await db.AuditLog.CountAsync(a => a.EventType == AuditEvents.DisciplineUnassigned && a.UserId == methodistRecord.Id));
        });
    }

    [Fact]
    public async Task Whoever_assigns_or_unassigns_themselves_gets_no_notification()
    {
        // Методист, который ещё и преподаватель, закрепляет за собой дисциплину и снимает закрепление.
        var (both, bothRecord, _) = await AccountAsync(App, [AppRoles.Teacher, AppRoles.Methodist], "Двойная Роль Обезличенный");
        var ids = await DisciplinesAsync("Б1.О.08");

        Assert.Equal(new AssignmentSummary(1, 0), await AssignedAsync(both, bothRecord, ids));
        await ExpectStatusAsync(HttpStatusCode.NoContent,
            both.DeleteAsync($"/api/discipline-assignments/{AssigneeOf(await PageAsync(both), "Б1.О.08", bothRecord).Id}"));

        Assert.Empty(await FeedAsync(both, NotificationEvents.DisciplineAssigned));
        Assert.Empty(await FeedAsync(both, NotificationEvents.DisciplineUnassigned));
    }

    [Fact]
    public async Task Assignment_is_refused_for_wrong_data_and_nothing_is_recorded()
    {
        var (methodist, methodistRecord) = await AccountAsync(App, AppRoles.Methodist);
        var (admin, _) = await AccountAsync(App, AppRoles.Admin);
        var (_, teacher) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var ids = await DisciplinesAsync("Б1.О.03");

        // Сотрудник — запись справочника с действующей учётной записью преподавателя; иначе 422.
        var (_, methodistOnly) = await AccountAsync(App, AppRoles.Methodist);
        var (_, disabled, disabledAccount) = await AccountAsync(App, [AppRoles.Teacher]);
        Assert.True((await admin.PostAsync($"/api/admin/users/{disabledAccount}/deactivate", null)).IsSuccessStatusCode);
        var (_, inactive) = await AccountAsync(App, AppRoles.Teacher);
        var noAccount = new Employee
        {
            ShortName = "Без Учётной З. З.", NameForDocument = "З. З. Без Учётной", CanLogin = false, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await App.WithDbAsync(async db =>
        {
            db.Employees.Add(noAccount);
            await db.SaveChangesAsync();
            await db.Employees.Where(e => e.Id == inactive.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false));
        });
        foreach (var (employee, title) in new[]
                 {
                     (Guid.NewGuid(), "Сотрудник не найден"), (Guid.Empty, "Сотрудник не найден"), (inactive.Id, "Сотрудник не найден"),
                     (noAccount.Id, "У сотрудника нет учётной записи преподавателя"),
                     (methodistOnly.Id, "У сотрудника нет учётной записи преподавателя"),
                     (disabled.Id, "У сотрудника нет учётной записи преподавателя")
                 })
        {
            var response = await methodist.PostAsJsonAsync("/api/discipline-assignments", new AssignRequest(employee, [.. ids]));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(title, await ProblemTitleAsync(response));
        }

        // Дисциплины: не выбрано ни одной, такой нет, строка плана — не дисциплина (модуль, группа элективов, практика, ГИА).
        foreach (var none in new List<Guid>?[] { null, [] })
        {
            var response = await methodist.PostAsJsonAsync("/api/discipline-assignments", new AssignRequest(teacher.Id, none));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Не выбраны дисциплины", await ProblemTitleAsync(response));
        }
        var unknown = await AssignAsync(methodist, teacher, ids[0], Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("Дисциплина не найдена", await ProblemTitleAsync(unknown));
        foreach (var code in new[] { Module, ElectiveGroup, Practice, Gia })
        {
            // Вместе с верной дисциплиной: отказ — на весь запрос, ничего не закрепляется.
            var response = await AssignAsync(methodist, teacher, ids[0], await DocumentData.DisciplineAsync(App, code));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("Для этой строки плана документ не создаётся", await ProblemTitleAsync(response));
        }

        await App.WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.DisciplineAssignments.CountAsync());
            Assert.Equal(0, await db.AuditLog.CountAsync(a => a.UserId == methodistRecord.Id && a.EventType == AuditEvents.DisciplineAssigned));
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientId == teacher.Id && n.EventType == NotificationEvents.DisciplineAssigned));
        });
    }

    [Fact]
    public async Task Only_methodists_and_admins_manage_assignments()
    {
        var (methodist, _) = await AccountAsync(App, AppRoles.Methodist);
        var (admin, _) = await AccountAsync(App, AppRoles.Admin);
        var (teacher, teacherRecord) = await AccountAsync(App, AppRoles.Teacher, "Закреплённый Первый Обезличенный");
        var (approver, _) = await AccountAsync(App, AppRoles.Approver);
        var ids = await DisciplinesAsync("Б1.О.03");
        await AssignedAsync(methodist, teacherRecord, ids);
        var existing = AssigneeOf(await PageAsync(methodist), "Б1.О.03", teacherRecord);
        var page = $"/api/discipline-assignments?planId={await App.RealPlanIdAsync()}";

        // Преподаватель, в том числе закреплённый, и согласующий — 403; без входа — 401.
        foreach (var client in new[] { teacher, approver })
        {
            await ExpectStatusAsync(HttpStatusCode.Forbidden, client.GetAsync(page));
            await ExpectStatusAsync(HttpStatusCode.Forbidden, AssignAsync(client, teacherRecord, ids));
            await ExpectStatusAsync(HttpStatusCode.Forbidden, client.DeleteAsync($"/api/discipline-assignments/{existing.Id}"));
        }
        var anonymous = App.CreateClientFor(null);
        await ExpectStatusAsync(HttpStatusCode.Unauthorized, anonymous.GetAsync(page));
        await ExpectStatusAsync(HttpStatusCode.Unauthorized, anonymous.DeleteAsync($"/api/discipline-assignments/{existing.Id}"));
        Assert.Equal(existing.Id, AssigneeOf(await PageAsync(methodist), "Б1.О.03", teacherRecord).Id);

        // Администратор закрепляет и снимает так же, как методист; плана, которого нет, нет и для него — 404.
        await ExpectStatusAsync(HttpStatusCode.OK, admin.GetAsync(page));
        await ExpectStatusAsync(HttpStatusCode.NoContent, admin.DeleteAsync($"/api/discipline-assignments/{existing.Id}"));
        Assert.Equal(new AssignmentSummary(1, 0), await AssignedAsync(admin, teacherRecord, ids));
        var unknownPlan = await methodist.GetAsync($"/api/discipline-assignments?planId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknownPlan.StatusCode);
        Assert.Equal("Учебный план не найден", await ProblemTitleAsync(unknownPlan));
    }
}
