using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RpdFos.Api.Features.Directory;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Направление у председателя УМК (этап 6.1, C6): правило оргструктуры в справочнике сотрудников — через API
/// администратора и в самой базе (<c>ck_users_direction_scope</c>, один действующий председатель на направление).
/// Как резолвер находит подписанта по направлению — <see cref="SignatoryResolutionTests"/>. Фикстуры обезличены.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UmkChairDirectionTests(DatabaseFixture app)
{
    private const string Records = "/api/admin/directory/records";

    [Fact]
    public async Task Chair_needs_one_direction_and_other_categories_have_none()
    {
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var (first, second) = (await DirectionAsync(), await DirectionAsync());
        var department = await DepartmentAsync();

        // Для формы: направления подготовки и привязка категории — у председателя УМК направление, у остальных нет.
        var units = (await admin.GetFromJsonAsync<DirectoryUnits>("/api/admin/directory/units"))!;
        Assert.Contains(units.Directions, d => d.Id == first.Id && d.Code == first.Code);
        Assert.Equal("direction", units.Roles.Single(r => r.Code == SignatoryRoles.UmkChair).Unit);
        Assert.DoesNotContain(units.Roles.Where(r => r.Code != SignatoryRoles.UmkChair), r => r.Unit == "direction");

        // Правило оргструктуры: у председателя — направление, без кафедры и института; у остальных направления нет.
        Assert.Equal("Не выбрано направление", (await RefusedAsync(admin, Chair("Умков У. У.", null), HttpStatusCode.UnprocessableEntity)).Title);
        Assert.Equal("Лишнее подразделение",
            (await RefusedAsync(admin, Chair("Умков У. У.", first.Id, department.Id), HttpStatusCode.UnprocessableEntity)).Title);
        Assert.Equal("Лишнее направление",
            (await RefusedAsync(admin, Chair("Ректоров Р. Р.", first.Id, role: SignatoryRoles.Rector), HttpStatusCode.UnprocessableEntity)).Title);
        Assert.Equal("Лишнее направление",
            (await RefusedAsync(admin, Chair("Доцентов Д. Д.", first.Id, role: null), HttpStatusCode.UnprocessableEntity)).Title);
        Assert.Equal("Направление не найдено",
            (await RefusedAsync(admin, Chair("Умков У. У.", Guid.NewGuid()), HttpStatusCode.UnprocessableEntity)).Title);

        var created = await admin.PostAsJsonAsync(Records, Chair("Умков У. У.", first.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var chair = (await created.Content.ReadFromJsonAsync<DirectoryEntry>())!;
        Assert.Equal((SignatoryRoles.UmkChair, first.Id, null, null), (chair.SignatoryRole, chair.DirectionId, chair.DepartmentId, chair.InstituteId));
        Assert.StartsWith(first.Code, chair.Unit);

        // На направление — один действующий председатель; на другое направление — свой.
        var occupied = await RefusedAsync(admin, Chair("Другой Д. Д.", first.Id), HttpStatusCode.Conflict);
        Assert.Equal("Место подписанта занято", occupied.Title);
        Assert.Contains("этого направления", occupied.Detail);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(Records, Chair("Другой Д. Д.", second.Id))).StatusCode);
    }

    [Fact]
    public async Task Direction_follows_the_chair_through_update_replace_and_change_of_category()
    {
        var (admin, adminRecord) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var (a, b, c) = (await DirectionAsync(), await DirectionAsync(), await DirectionAsync());
        var chairA = await CreatedAsync(admin, Chair("Умков А. А.", a.Id));
        var chairB = await CreatedAsync(admin, Chair("Умков Б. Б.", b.Id));

        // Перевести председателя на другое направление можно, если там никого нет; занятое направление — 409.
        Assert.Equal("Место подписанта занято",
            (await RefusedAsync(admin, Chair("Умков А. А.", b.Id), HttpStatusCode.Conflict, $"{Records}/{chairA.Id}", HttpMethod.Put)).Title);
        var moved = await admin.PutAsJsonAsync($"{Records}/{chairA.Id}", Chair("Умков А. А.", c.Id));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(c.Id, (await moved.Content.ReadFromJsonAsync<DirectoryEntry>())!.DirectionId);
        // Освободившееся направление принимает нового председателя.
        var newcomer = await CreatedAsync(admin, Chair("Умков Н. Н.", a.Id));

        // Деактивированная запись место не держит; вернуть её нельзя, пока направление занято другим.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Records}/{newcomer.Id}/deactivate", null)).StatusCode);
        var successor = await CreatedAsync(admin, Chair("Умков С. С.", a.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{Records}/{newcomer.Id}/activate", null)).StatusCode);

        // «Сменить сотрудника на должности»: направление переходит к преемнику вместе с категорией.
        var replaced = await admin.PostAsJsonAsync($"{Records}/{chairB.Id}/replace",
            new ReplaceRequest(null, new EmployeeRequest("Преемников П. П.", null, null, null, null, null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var heir = (await replaced.Content.ReadFromJsonAsync<DirectoryEntry>())!;
        Assert.Equal((SignatoryRoles.UmkChair, b.Id, true), (heir.SignatoryRole, heir.DirectionId, heir.IsActive));

        // Смена категории снимает направление: «не подписант» — без направления, с направлением — отказ.
        Assert.Equal("Лишнее направление",
            (await RefusedAsync(admin, Chair("Умков С. С.", a.Id, role: null), HttpStatusCode.UnprocessableEntity, $"{Records}/{successor.Id}", HttpMethod.Put)).Title);
        var demoted = await admin.PutAsJsonAsync($"{Records}/{successor.Id}", Chair("Умков С. С.", null, role: null));
        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        var demotedEntry = (await demoted.Content.ReadFromJsonAsync<DirectoryEntry>())!;
        Assert.Equal((null, null), (demotedEntry.SignatoryRole, demotedEntry.DirectionId));

        await app.WithDbAsync(async db =>
        {
            // Прежний председатель направления B освобождён вместе с направлением.
            var previous = await db.Employees.SingleAsync(e => e.Id == chairB.Id);
            Assert.Equal((false, null, null), (previous.IsActive, previous.SignatoryRole, previous.DirectionId));
            var demotedRecord = await db.Employees.SingleAsync(e => e.Id == successor.Id);
            Assert.Equal((null, null), (demotedRecord.SignatoryRole, demotedRecord.DirectionId));
            // В журнале аудита — направление: было и стало.
            var audit = (await db.AuditLog.Where(x => x.EntityId == chairA.Id && x.EventType == AuditEvents.EmployeeUpdated).ToListAsync()).Single();
            Assert.Contains(a.Id.ToString(), audit.Payload);
            Assert.Contains(c.Id.ToString(), audit.Payload);
            Assert.Equal(adminRecord.Id, audit.UserId);
        });
    }

    [Fact]
    public async Task Chair_without_direction_is_listed_and_saved_only_with_one()
    {
        // Запись без направления — такую создаёт и файл должностей: миграцию и список она не ломает, но подписать не может.
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        var direction = await DirectionAsync();
        Employee? legacy = null;
        await app.WithDbAsync(async db => legacy = await ChairData.AddChairAsync(db, null));

        var listed = (await admin.GetFromJsonAsync<List<DirectoryEntry>>($"{Records}?role={SignatoryRoles.UmkChair}"))!.Single(e => e.Id == legacy!.Id);
        Assert.Equal((SignatoryRoles.UmkChair, null, null), (listed.SignatoryRole, listed.DirectionId, listed.Unit));

        // Деактивировать и вернуть её можно как есть; сохранить правку — только с выбранным направлением.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Records}/{legacy!.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Records}/{legacy.Id}/activate", null)).StatusCode);
        var url = $"{Records}/{legacy.Id}";
        Assert.Equal("Не выбрано направление", (await RefusedAsync(admin, Chair(legacy.ShortName, null), HttpStatusCode.UnprocessableEntity, url, HttpMethod.Put)).Title);
        var saved = await admin.PutAsJsonAsync(url, Chair(legacy.ShortName, direction.Id));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(direction.Id, (await saved.Content.ReadFromJsonAsync<DirectoryEntry>())!.DirectionId);
    }

    [Fact]
    public async Task Database_keeps_direction_only_with_umk_chair_and_one_active_chair_per_direction()
    {
        var direction = await DirectionAsync();
        var department = await DepartmentAsync();
        await app.WithDbAsync(async db => await ChairData.AddChairAsync(db, direction.Id));

        // Направление бывает только у председателя УМК; у председателя — без кафедры и института.
        Assert.Equal("ck_users_direction_scope", await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.Rector, direction.Id)));
        Assert.Equal("ck_users_direction_scope", await RejectedByAsync(ChairData.NewEmployee(null, direction.Id)));
        Assert.Equal("ck_users_signatory_scope", await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.UmkChair, direction.Id, department.Id)));
        Assert.Equal("fk_users_study_directions_direction_id", await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.UmkChair, Guid.NewGuid())));

        // Один действующий председатель на направление; неактивные записи прежних председателей не мешают.
        Assert.Equal("ix_users_signatory_role_direction_id", await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.UmkChair, direction.Id)));
        Assert.Null(await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.UmkChair, direction.Id, active: false)));
        // Записи без направления — как в базе до этого шага: их может быть несколько.
        Assert.Null(await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.UmkChair)));
        Assert.Null(await RejectedByAsync(ChairData.NewEmployee(SignatoryRoles.UmkChair)));
    }

    [Fact]
    public async Task Positions_file_resets_direction_of_a_record_it_reclassifies()
    {
        // Файл должностей определяет категорию заново, а направления в нём нет. Запись с должностью из файла, ставшая
        // председателем УМК, после загрузки снова с категорией из файла — и без направления, иначе загрузка нарушила бы
        // ck_users_direction_scope и не выполнилась.
        var (admin, _) = await DocumentData.UserAsync(app, AppRoles.Admin);
        await app.RealPlanIdAsync(); // кафедры — из справочника кафедр плана
        Assert.Contains((await UploadPositionsAsync(admin)).Status, new[] { ParserRunStatuses.Succeeded, ParserRunStatuses.Partial });
        var direction = await DirectionAsync();
        Guid recordId = Guid.Empty;
        await app.WithDbAsync(async db =>
        {
            var record = await db.Employees.Where(e => e.IsActive && e.SignatoryRole == SignatoryRoles.UmuHead && e.OfficialDesignation != null)
                .OrderBy(e => e.ShortName).FirstAsync();
            (recordId, record.SignatoryRole, record.DirectionId) = (record.Id, SignatoryRoles.UmkChair, direction.Id);
            await db.SaveChangesAsync();
        });

        var run = await UploadPositionsAsync(admin);

        Assert.Contains(run.Status, new[] { ParserRunStatuses.Succeeded, ParserRunStatuses.Partial });
        await app.WithDbAsync(async db =>
        {
            var record = await db.Employees.SingleAsync(e => e.Id == recordId);
            Assert.Equal((SignatoryRoles.UmuHead, null), (record.SignatoryRole, record.DirectionId));
        });
    }

    private static async Task<ImportItem> UploadPositionsAsync(HttpClient admin)
    {
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
        return run;
    }

    // Ограничение базы, которое отвергло запись; null — запись сохранена.
    private async Task<string?> RejectedByAsync(Employee employee)
    {
        string? constraint = null;
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(employee);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException postgres)
            {
                constraint = postgres.ConstraintName;
            }
        });
        return constraint;
    }

    private static EmployeeRequest Chair(string shortName, Guid? directionId, Guid? departmentId = null, string? role = SignatoryRoles.UmkChair) =>
        new(shortName, null, null, null, "Председатель УМК", null, null, role, departmentId, null, directionId);

    private static async Task<DirectoryEntry> CreatedAsync(HttpClient admin, EmployeeRequest request)
    {
        var response = await admin.PostAsJsonAsync(Records, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DirectoryEntry>())!;
    }

    private static async Task<ProblemDetails> RefusedAsync(HttpClient admin, EmployeeRequest request, HttpStatusCode status,
        string? url = null, HttpMethod? method = null)
    {
        var response = method == HttpMethod.Put
            ? await admin.PutAsJsonAsync(url!, request)
            : await admin.PostAsJsonAsync(url ?? Records, request);
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }

    private async Task<ChairDirection> DirectionAsync()
    {
        ChairDirection? direction = null;
        await app.WithDbAsync(async db => direction = await ChairData.AddDirectionAsync(db));
        return direction!;
    }

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
