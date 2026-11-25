using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Departments;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Страница «Кафедры» администратора (этап 8, B2; FR-ADM-06): правка названия и краткого названия с записью в аудит,
/// отказы, доступ, исправленное название у новых документов и прежнее — у созданных. Импорт, который исправленное название
/// не переписывает, проверяет <c>ImportTests</c> (у него своя база).
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DepartmentsApiTests(DatabaseFixture app)
{
    private const string Url = "/api/admin/departments";

    private async Task<Department> AddDepartmentAsync()
    {
        var code = Guid.NewGuid().ToString("N")[..8];
        var department = new Department { Code = code, Name = $"Кафедра проверки {code}", ShortName = $"КП{code}" };
        await app.WithDbAsync(async db =>
        {
            db.Departments.Add(department);
            await db.SaveChangesAsync();
        });
        return department;
    }

    [Fact]
    public async Task Edit_writes_audit_marks_department_and_rejects_bad_requests()
    {
        var (admin, adminRecord) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var department = await AddDepartmentAsync();
        var other = await AddDepartmentAsync();
        var name = $"Кафедра исправленная {department.Code}";
        var shortName = $"И{department.Code}";

        var saved = await admin.PutAsJsonAsync($"{Url}/{department.Id}", new DepartmentRequest($"  {name}  ", shortName));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var item = (await saved.Content.ReadFromJsonAsync<DepartmentItem>())!;
        Assert.Equal((name, shortName, department.Code, true), (item.Name, item.ShortName, item.Code, item.IsEditedManually));

        // В списке — исправленная кафедра с признаком; неисправленная — без него.
        var list = (await admin.GetFromJsonAsync<List<DepartmentItem>>(Url))!;
        Assert.True(list.Single(d => d.Id == department.Id).IsEditedManually);
        Assert.False(list.Single(d => d.Id == other.Id).IsEditedManually);

        // Аудит: кто, что было, что стало.
        await app.WithDbAsync(async db =>
        {
            var entry = await db.AuditLog.SingleAsync(a => a.EntityId == department.Id);
            Assert.Equal((AuditEvents.DepartmentUpdated, AuditEntities.Department, adminRecord.Id), (entry.EventType, entry.EntityType, entry.UserId));
            using var payload = JsonDocument.Parse(entry.Payload);
            var (was, now) = (payload.RootElement.GetProperty("before"), payload.RootElement.GetProperty("after"));
            Assert.Equal((department.Name, department.ShortName), (was.GetProperty("name").GetString(), was.GetProperty("short_name").GetString()));
            Assert.Equal((name, shortName), (now.GetProperty("name").GetString(), now.GetProperty("short_name").GetString()));
            Assert.NotEqual(default, entry.OccurredAt);
        });

        // Журнал аудита называет событие и кафедру словами.
        var page = (await admin.GetFromJsonAsync<AuditPage>($"/api/audit?userId={adminRecord.Id}&eventType={AuditEvents.DepartmentUpdated}&skip=0&take=50"))!;
        var record = Assert.Single(page.Items);
        Assert.Equal(("Исправлено название кафедры", name), (record.Title, record.EntityTitle));
        Assert.Contains(record.Details, d => d.Contains($"стало «{name}»"));

        // Правка без изменений — не правка: новой записи аудита нет.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Url}/{department.Id}", new DepartmentRequest(name, shortName))).StatusCode);
        await app.WithDbAsync(async db => Assert.Equal(1, await db.AuditLog.CountAsync(a => a.EntityId == department.Id)));

        // Название и краткое название уникальны; пустое и слишком длинное — 422; неизвестная кафедра — 404.
        var sameName = await admin.PutAsJsonAsync($"{Url}/{other.Id}", new DepartmentRequest(name, other.ShortName));
        var sameShort = await admin.PutAsJsonAsync($"{Url}/{other.Id}", new DepartmentRequest(other.Name, shortName));
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal("Название занято", (await sameName.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.Conflict, sameShort.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"{Url}/{other.Id}", new DepartmentRequest(" ", "К"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"{Url}/{other.Id}", new DepartmentRequest(new string('а', 256), "К"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"{Url}/{other.Id}", new DepartmentRequest("Название", new string('а', 51)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Url}/{Guid.NewGuid()}", new DepartmentRequest("Название", "К"))).StatusCode);
        await app.WithDbAsync(async db =>
        {
            var untouched = await db.Departments.SingleAsync(d => d.Id == other.Id);
            Assert.Equal((other.Name, other.ShortName, false), (untouched.Name, untouched.ShortName, untouched.IsEditedManually));
        });
    }

    [Theory]
    [InlineData(AppRoles.Methodist)]
    [InlineData(AppRoles.Teacher)]
    [InlineData(AppRoles.Approver)]
    public async Task Only_admin_may_read_and_edit(string role)
    {
        var department = await AddDepartmentAsync();
        var client = app.CreateClientFor(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Url}/{department.Id}", new DepartmentRequest("Другое", "Д"))).StatusCode);
        await app.WithDbAsync(async db => Assert.False((await db.Departments.SingleAsync(d => d.Id == department.Id)).IsEditedManually));
    }

    [Fact]
    public async Task Corrected_name_goes_to_new_documents_and_created_ones_keep_snapshot()
    {
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var template = await DocumentData.PublishedTemplateAsync(app);
        var (teacher, _) = await DocumentData.UserAsync(app);

        // Кафедра дисциплины реального плана: после проверки название возвращается как было — база общая для тестов.
        const string code = "Б1.О.10";
        Guid departmentId = default;
        Department original = null!;
        await app.WithDbAsync(async db =>
        {
            departmentId = (await db.Disciplines.AsNoTracking().SingleAsync(d => d.CurriculumPlanId == template.CurriculumPlanId && d.Code == code)).DepartmentId!.Value;
            original = await db.Departments.AsNoTracking().SingleAsync(d => d.Id == departmentId);
        });
        try
        {
            var before = await DocumentData.CreateAsync(app, teacher, template.Id, code);
            var corrected = original.Name + " (исправлено)";
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Url}/{departmentId}", new DepartmentRequest(corrected, original.ShortName))).StatusCode);

            // Созданный до правки документ остаётся с прежним названием — как в снимке.
            var reread = (await teacher.GetFromJsonAsync<DocumentDetails>($"/api/documents/{before.Document.Id}"))!;
            Assert.Equal(original.Name, reread.Payload.GetProperty("title_page").GetProperty("department_name").GetString());

            // Новый документ той же дисциплины (черновик прежнего удалён) — с исправленным.
            Assert.Equal(HttpStatusCode.NoContent, (await teacher.DeleteAsync($"/api/documents/{before.Document.Id}?version={before.Document.Version}")).StatusCode);
            var created = await DocumentData.CreateAsync(app, teacher, template.Id, code);
            Assert.Equal(corrected, created.Payload.GetProperty("title_page").GetProperty("department_name").GetString());
            await teacher.DeleteAsync($"/api/documents/{created.Document.Id}?version={created.Document.Version}");
        }
        finally
        {
            await app.WithDbAsync(async db =>
            {
                var department = await db.Departments.SingleAsync(d => d.Id == departmentId);
                (department.Name, department.ShortName, department.IsEditedManually) = (original.Name, original.ShortName, original.IsEditedManually);
                await db.SaveChangesAsync();
            });
        }
    }
}
