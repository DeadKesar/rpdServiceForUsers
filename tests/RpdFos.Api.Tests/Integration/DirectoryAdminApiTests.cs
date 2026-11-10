using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Directory;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Этап 5.2, B5: справочник сотрудников на странице администратора — правила (уникальность официального названия,
/// место подписанта, оргструктура, смена человека на должности), журнал аудита, загрузка файла должностей, доступ,
/// учётные записи (временный пароль, отключение). Фикстуры обезличены.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DirectoryAdminApiTests(DatabaseFixture app)
{
    private const string Records = "/api/admin/directory/records";

    [Fact]
    public async Task Rules_official_designation_seat_and_org_structure()
    {
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var department = await DepartmentAsync();
        var designation = $"Кафедра {department.Code} — заведующий";

        var head = await admin.PostAsJsonAsync(Records, Request("Первый П. П.", designation, SignatoryRoles.DepartmentHead, department.Id));
        Assert.Equal(HttpStatusCode.Created, head.StatusCode);
        var created = (await head.Content.ReadFromJsonAsync<DirectoryEntry>())!;
        // Подпись по умолчанию — из «Фамилия И. О.».
        Assert.Equal(("П. П. Первый", "Заведующий кафедрой"), (created.NameForDocument, created.SignatoryRoleTitle));

        // Официальное название уникально среди активных; место подписанта — одно на кафедру.
        var sameDesignation = await admin.PostAsJsonAsync(Records, Request("Второй В. В.", designation, null, null));
        var sameSeat = await admin.PostAsJsonAsync(Records, Request("Второй В. В.", null, SignatoryRoles.DepartmentHead, department.Id));
        // Оргструктура: заведующий — с кафедрой, подписант уровня университета — без подразделения.
        var headWithoutDepartment = await admin.PostAsJsonAsync(Records, Request("Третий Т. Т.", null, SignatoryRoles.DepartmentHead, null));
        var rectorWithDepartment = await admin.PostAsJsonAsync(Records, Request("Третий Т. Т.", null, SignatoryRoles.Rector, department.Id));

        Assert.Equal(HttpStatusCode.Conflict, sameDesignation.StatusCode);
        Assert.Equal("Официальное название занято", (await sameDesignation.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.Conflict, sameSeat.StatusCode);
        Assert.Equal("Место подписанта занято", (await sameSeat.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, headWithoutDepartment.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rectorWithDepartment.StatusCode);

        // Деактивированная запись место не держит: вернуть её нельзя, пока место занято другой.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Records}/{created.Id}/deactivate", null)).StatusCode);
        var successor = await admin.PostAsJsonAsync(Records, Request("Второй В. В.", designation, SignatoryRoles.DepartmentHead, department.Id));
        Assert.Equal(HttpStatusCode.Created, successor.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{Records}/{created.Id}/activate", null)).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var events = await db.AuditLog.Where(a => a.EntityId == created.Id).Select(a => a.EventType).ToListAsync();
            Assert.Equal([AuditEvents.EmployeeCreated, AuditEvents.EmployeeDeactivated], events.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task Replace_person_on_position_moves_the_seat()
    {
        var (admin, adminRecord) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var department = await DepartmentAsync();
        var designation = $"Кафедра {department.Code} — заведующий";
        var holder = (await (await admin.PostAsJsonAsync(Records,
            Request("Прежний П. П.", designation, SignatoryRoles.DepartmentHead, department.Id))).Content.ReadFromJsonAsync<DirectoryEntry>())!;

        // Новый сотрудник: место переходит к нему, прежняя запись без учётной записи — неактивна.
        var replaced = await admin.PostAsJsonAsync($"{Records}/{holder.Id}/replace",
            new ReplaceRequest(null, new EmployeeRequest("Новый Н. Н.", null, "Новый Николай Николаевич", null, null, null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var successor = (await replaced.Content.ReadFromJsonAsync<DirectoryEntry>())!;
        Assert.Equal((designation, SignatoryRoles.DepartmentHead, department.Id, true),
            (successor.OfficialDesignation, successor.SignatoryRole, successor.DepartmentId, successor.IsActive));

        // Сотрудник с учётной записью, переданный дальше: остаётся активным, но без места.
        await app.WithDbAsync(async db =>
        {
            var record = await db.Employees.SingleAsync(e => e.Id == successor.Id);
            record.IdentitySubject = Guid.NewGuid().ToString();
            record.Email = $"{record.IdentitySubject}@example.test";
            record.CanLogin = true;
            await db.SaveChangesAsync();
        });
        var other = await admin.PostAsJsonAsync(Records, Request("Третий Т. Т.", null, null, department.Id));
        var third = (await other.Content.ReadFromJsonAsync<DirectoryEntry>())!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Records}/{successor.Id}/replace", new ReplaceRequest(third.Id, null))).StatusCode);

        await app.WithDbAsync(async db =>
        {
            var previous = await db.Employees.SingleAsync(e => e.Id == holder.Id);
            Assert.Equal((false, null, null), (previous.IsActive, previous.OfficialDesignation, previous.SignatoryRole));
            var withAccount = await db.Employees.SingleAsync(e => e.Id == successor.Id);
            Assert.Equal((true, null, null), (withAccount.IsActive, withAccount.OfficialDesignation, withAccount.SignatoryRole));
            var current = await db.Employees.SingleAsync(e => e.Id == third.Id);
            Assert.Equal((designation, SignatoryRoles.DepartmentHead), (current.OfficialDesignation, current.SignatoryRole));
            Assert.Contains(await db.AuditLog.Where(a => a.EntityId == holder.Id).ToListAsync(),
                a => a.EventType == AuditEvents.EmployeeReplaced && a.UserId == adminRecord.Id && a.Payload.Contains("before"));
        });
    }

    [Fact]
    public async Task Admin_uploads_positions_file_and_it_is_imported_in_background()
    {
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        await app.RealPlanIdAsync(); // кафедры — из справочника кафедр плана
        var file = ImportFiles.Read()[3];
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file.Content);
        content.Headers.ContentType = new MediaTypeHeaderValue(ImportFiles.All[3].Mime);
        form.Add(content, "file", file.Name);

        var response = await admin.PostAsync("/api/admin/directory/imports", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<ImportItem>())!;
        for (var i = 0; i < 100 && run.Status == ParserRunStatuses.Running; i++)
        {
            await Task.Delay(200);
            run = (await admin.GetFromJsonAsync<ImportItem>($"/api/admin/directory/imports/{run.Id}"))!;
        }
        Assert.Contains(run.Status, new[] { ParserRunStatuses.Succeeded, ParserRunStatuses.Partial });
        Assert.Equal(("positions-anonymized.xlsx", 37, 37), (run.FileName, run.Rows, run.Added + run.Updated));
        var last = (await admin.GetFromJsonAsync<ImportItem>("/api/admin/directory/imports/last"))!;
        Assert.Equal(run.Id, last.Id);
        // Заведующий кафедрой из файла — с кафедрой из справочника плана.
        var heads = await admin.GetFromJsonAsync<List<DirectoryEntry>>($"{Records}?role={SignatoryRoles.DepartmentHead}");
        Assert.Contains(heads!, h => h.OfficialDesignation == "Информационные технологии и вычислительные системы" && h.DepartmentId is not null);
    }

    [Fact]
    public async Task Methodist_cannot_use_admin_directory()
    {
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);

        Assert.Equal(HttpStatusCode.Forbidden, (await methodist.GetAsync(Records)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await methodist.PostAsJsonAsync(Records, Request("Иной И. И.", null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await methodist.PostAsync("/api/admin/directory/imports", new MultipartFormDataContent())).StatusCode);
    }

    [Fact]
    public async Task Admin_sets_temporary_password_and_disables_account()
    {
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var email = $"user-{Guid.NewGuid():N}@example.test";
        var created = await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest(email, "Сотрудник Обезличенный", "Start#2026a", [AppRoles.Teacher]));
        var user = (await created.Content.ReadFromJsonAsync<UserItem>())!;

        var weak = await admin.PostAsJsonAsync($"/api/admin/users/{user.Id}/password", new PasswordRequest("123"));
        var reset = await admin.PostAsJsonAsync($"/api/admin/users/{user.Id}/password", new PasswordRequest("Temp#2026b"));
        var disabled = await admin.PostAsync($"/api/admin/users/{user.Id}/deactivate", null);
        var afterDisable = (await admin.GetFromJsonAsync<List<AccountItem>>("/api/admin/users"))!.Single(a => a.Id == user.Id);

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.False(afterDisable.IsActive);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var account = (await users.FindByIdAsync(user.Id.ToString()))!;
            Assert.True(await users.CheckPasswordAsync(account, "Temp#2026b"));
            Assert.True(await users.IsLockedOutAsync(account));
        }
        // Запись справочника при этом не меняется.
        await app.WithDbAsync(async db => Assert.True((await db.Employees.SingleAsync(e => e.Id == user.DirectoryUserId)).IsActive));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{user.Id}/activate", null)).StatusCode);
        Assert.True((await admin.GetFromJsonAsync<List<AccountItem>>("/api/admin/users"))!.Single(a => a.Id == user.Id).IsActive);
    }

    private static EmployeeRequest Request(string shortName, string? designation, string? role, Guid? departmentId) =>
        new(shortName, null, null, null, role is null ? "Доцент" : "Заведующий кафедрой", null, designation, role, departmentId, null);

    // Своя кафедра теста: места подписантов других тестов не мешают.
    private async Task<Department> DepartmentAsync()
    {
        var code = Random.Shared.Next(10000, 99999).ToString();
        var department = new Department { Code = code, Name = $"Кафедра проверки {code}", ShortName = $"КП{code}" };
        await app.WithDbAsync(async db =>
        {
            db.Departments.Add(department);
            await db.SaveChangesAsync();
        });
        return department;
    }
}
