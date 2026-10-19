using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Directory;
using RpdFos.Api.Features.Users;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Учётная запись ↔ справочник сотрудников: создание записи справочника или привязка к существующей.</summary>
[Collection(DatabaseCollection.Name)]
public class UsersApiTests(DatabaseFixture app)
{
    private const string Password = "Test-Passw0rd";

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@test.local";

    private Task<HttpResponseMessage> CreateAsync(CreateUserRequest rq) =>
        app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync("/api/admin/users", rq);

    [Fact]
    public async Task New_account_gets_its_own_directory_record()
    {
        var email = NewEmail();
        var response = await CreateAsync(new CreateUserRequest(email, "Петров Пётр Петрович", Password, AppRoles.Teacher));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<UserItem>())!;
        await app.WithDbAsync(async db =>
        {
            var e = await db.Employees.SingleAsync(x => x.Id == item.DirectoryUserId);
            Assert.Equal((item.Id.ToString(), true, true, email), (e.IdentitySubject, e.CanLogin, e.IsActive, e.Email));
            Assert.Equal(("Петров Пётр Петрович", "Петров П. П.", "П. П. Петров"), (e.FullName, e.ShortName, e.NameForDocument));
        });
    }

    [Fact]
    public async Task Account_can_be_linked_to_existing_directory_record()
    {
        // Запись справочника из файла должностей: без входа, только «Фамилия И. О.».
        var record = new Employee
        {
            ShortName = "Сидоров С. С.", NameForDocument = "С. С. Сидоров", OfficialDesignation = $"Кафедра {Guid.NewGuid():N}",
            Position = "Заведующий кафедрой", CanLogin = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(record);
            await db.SaveChangesAsync();
        });

        var email = NewEmail();
        var response = await CreateAsync(new CreateUserRequest(email, "Сидоров Семён Семёнович", Password, AppRoles.Teacher, record.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<UserItem>())!;
        Assert.Equal(record.Id, item.DirectoryUserId);
        await app.WithDbAsync(async db =>
        {
            var e = await db.Employees.SingleAsync(x => x.Id == record.Id);
            Assert.Equal((item.Id.ToString(), true, email), (e.IdentitySubject, e.CanLogin, e.Email));
            // ФИО полностью — из учётной записи; подпись и должность — как в справочнике.
            Assert.Equal(("Сидоров Семён Семёнович", "Сидоров С. С.", "Заведующий кафедрой"), (e.FullName, e.ShortName, e.Position));
            Assert.Equal(1, await db.Employees.CountAsync(x => x.Email == email));
        });

        // Вторую учётную запись к той же записи не привязать — и сама учётная запись не остаётся.
        var otherEmail = NewEmail();
        var again = await CreateAsync(new CreateUserRequest(otherEmail, "Сидоров Семён Семёнович", Password, AppRoles.Teacher, record.Id));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.Users.AnyAsync(u => u.Email == otherEmail)));
    }

    [Fact]
    public async Task Unknown_directory_record_is_404_and_account_is_not_created()
    {
        var email = NewEmail();
        var response = await CreateAsync(new CreateUserRequest(email, "Иванов Иван Иванович", Password, AppRoles.Teacher, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.WithDbAsync(async db => Assert.False(await db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Accounts_list_shows_role_and_directory_record()
    {
        var email = NewEmail();
        var created = (await (await CreateAsync(new CreateUserRequest(email, "Орлов Олег Олегович", Password, AppRoles.Methodist)))
            .Content.ReadFromJsonAsync<UserItem>())!;

        var list = await app.CreateClientFor(AppRoles.Admin).GetFromJsonAsync<List<AccountItem>>("/api/admin/users");

        var item = Assert.Single(list!, a => a.Email == email);
        Assert.Equal((created.Id, "Орлов Олег Олегович", AppRoles.Methodist), (item.Id, item.FullName, item.Role));
        Assert.Equal((created.DirectoryUserId, "Орлов О. О."), (item.Directory!.Id, item.Directory.ShortName));
    }

    [Fact]
    public async Task Directory_search_finds_active_unlinked_records_by_surname()
    {
        // Три записи с одной основой фамилии: подходит только активная без учётной записи.
        var stem = "Зеленц" + Guid.NewGuid().ToString("N")[..6];
        Employee Record(string surname, bool linked = false, bool active = true) => new()
        {
            ShortName = $"{surname} З. З.", NameForDocument = $"З. З. {surname}", CanLogin = linked, IsActive = active,
            IdentitySubject = linked ? Guid.NewGuid().ToString() : null, Email = linked ? NewEmail() : null,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var free = Record(stem + "ов");
        await app.WithDbAsync(async db =>
        {
            db.Employees.AddRange(free, Record(stem + "ин", linked: true), Record(stem + "ев", active: false));
            await db.SaveChangesAsync();
        });

        var found = await app.CreateClientFor(AppRoles.Admin)
            .GetFromJsonAsync<List<DirectoryRecordItem>>($"/api/admin/directory?surname={stem.ToUpperInvariant()}");

        Assert.Equal([free.Id], found!.Select(r => r.Id));
    }

    [Theory]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin/directory?surname=А")]
    public async Task Teacher_cannot_read_accounts_or_directory(string url)
    {
        var response = await app.CreateClientFor(AppRoles.Teacher).GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_created_before_directory_are_linked()
    {
        // Учётная запись этапа 1: создана в обход UsersModule, записи справочника у неё нет.
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = NewEmail();
        var account = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Смирнов Степан Сергеевич" };
        Assert.True((await users.CreateAsync(account, Password)).Succeeded);
        var directory = scope.ServiceProvider.GetRequiredService<DirectoryAccounts>();
        ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim(DirectoryAccounts.SubjectClaim, account.Id.ToString())], "test"));
        Assert.Null(await directory.FindIdAsync(User()));

        var (linked, _) = await directory.LinkUnlinkedAccountsAsync();

        Assert.True(linked >= 1);
        var id = await directory.FindIdAsync(User());
        Assert.NotNull(id);
        await app.WithDbAsync(async db =>
        {
            var e = await db.Employees.SingleAsync(x => x.Id == id);
            Assert.Equal(("Смирнов С. С.", "С. С. Смирнов", true, email), (e.ShortName, e.NameForDocument, e.CanLogin, e.Email));
        });

        // Повторный запуск (каждый старт в Development) уже связанную запись не трогает.
        await directory.LinkUnlinkedAccountsAsync();
        await app.WithDbAsync(async db => Assert.Equal(1, await db.Employees.CountAsync(x => x.Email == email)));
    }

    [Fact]
    public async Task Directory_record_of_current_user_is_found_by_sub()
    {
        var response = await CreateAsync(new CreateUserRequest(NewEmail(), "Кузнецов Кирилл Кириллович", Password, AppRoles.Methodist));
        var item = (await response.Content.ReadFromJsonAsync<UserItem>())!;

        await using var scope = app.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<DirectoryAccounts>();
        ClaimsPrincipal User(string sub) => new(new ClaimsIdentity([new Claim(DirectoryAccounts.SubjectClaim, sub)], "test"));

        Assert.Equal(item.DirectoryUserId, await directory.FindIdAsync(User(item.Id.ToString())));
        Assert.Null(await directory.FindIdAsync(User(Guid.NewGuid().ToString())));
    }
}
