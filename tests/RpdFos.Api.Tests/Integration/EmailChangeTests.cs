using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Profile;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Смена email с подтверждением (этап 8, A8): запрос — через API профиля от имени учётной записи, подтверждение — страницей
/// «Смена email» как в жизни (форма с защитным токеном), ссылки берутся из писем, которые поймал <see cref="FakeMailSender"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public partial class EmailChangeTests(DatabaseFixture app)
{
    private const string Password = "Test-Passw0rd";

    private FakeMailSender Mail => app.Services.GetRequiredService<FakeMailSender>();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [GeneratedRegex(@"/Account/ConfirmEmail#t=(\S+)")]
    private static partial Regex LinkInLetter();

    private static string NewAddress() => $"changed-{Guid.NewGuid():N}@test.local";

    // Преподаватель с записью справочника: вход и профиль — от его имени.
    private Task<(HttpClient Client, Employee Employee, string Email, Guid AccountId)> NewAccountAsync() =>
        AccountData.CreateAsync(app, [AppRoles.Teacher]);

    private static async Task<HttpResponseMessage> RequestAsync(HttpClient client, string? email) =>
        await client.PostAsJsonAsync("/api/profile/email-change", new EmailChangeRequestBody(email));

    private static string TokenOf(OutgoingMail letter)
    {
        var match = LinkInLetter().Match(letter.Body);
        Assert.True(match.Success, "В письме нет ссылки на страницу «Смена email».");
        return match.Groups[1].Value;
    }

    // Страница подтверждения: открыть, взять защитный токен, отправить форму с токеном ссылки (его скрипт берёт из фрагмента).
    private async Task<string> ConfirmAsync(string token)
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var form = await browser.GetStringAsync("/Account/ConfirmEmail");
        var response = await browser.PostAsync("/Account/ConfirmEmail", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryField().Match(form).Groups[1].Value,
            ["Token"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebHtml(await response.Content.ReadAsStringAsync());
    }

    private static string WebHtml(string html) => WebUtility.HtmlDecode(html);

    // Оба письма запроса: на прежний и на новый адрес; у каждого своя ссылка.
    private (string Old, string New) Links(string oldEmail, string newEmail) =>
        (TokenOf(Assert.Single(Mail.SentTo(oldEmail))), TokenOf(Assert.Single(Mail.SentTo(newEmail))));

    private async Task<T> WithUsersAsync<T>(Func<UserManager<AppUser>, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private Task<bool> CanLogInAsync(string email, string password) => WithUsersAsync(async users =>
        await users.FindByEmailAsync(email) is { } user && await users.CheckPasswordAsync(user, password)
        && await users.FindByNameAsync(email) is not null);

    private async Task<string?> DirectoryEmailAsync(Guid employeeId)
    {
        string? email = null;
        await app.WithDbAsync(async db => email = (await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId)).Email);
        return email;
    }

    private async Task<int> RequestCountAsync(Guid accountId)
    {
        var count = 0;
        await app.WithDbAsync(async db => count = await db.EmailChangeRequests.CountAsync(r => r.UserId == accountId));
        return count;
    }

    [Fact]
    public async Task Both_confirmations_change_account_and_directory_email_and_login_moves_to_new_address()
    {
        var (client, employee, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();

        var response = await RequestAsync(client, newEmail);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        Assert.NotEqual(oldToken, newToken);

        Assert.Contains("Подтверждено с прежнего адреса", await ConfirmAsync(oldToken));
        Assert.True(await CanLogInAsync(oldEmail, Password));
        Assert.False(await CanLogInAsync(newEmail, Password));

        Assert.Contains($"Адрес изменён на {newEmail}", await ConfirmAsync(newToken));

        Assert.True(await CanLogInAsync(newEmail, Password));
        Assert.False(await CanLogInAsync(oldEmail, Password));
        Assert.Equal(newEmail, await DirectoryEmailAsync(employee.Id));
        Assert.Equal(0, await RequestCountAsync(accountId));
        var profile = (await (await client.GetAsync("/api/profile")).Content.ReadFromJsonAsync<ProfileItem>())!;
        Assert.Equal(newEmail, profile.Email);
        Assert.Null(profile.EmailChange);
    }

    // «Войти» после смены адреса ведёт сразу ко входу клиента: на его начальной странице «Войдите» пришлось бы нажимать ещё раз
    // (замечание точки проверки A9).
    [Fact]
    public async Task Sign_in_link_after_the_change_starts_the_client_login()
    {
        var (client, _, oldEmail, _) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        await ConfirmAsync(oldToken);

        Assert.Contains("<a href=\"/authentication/login\">Войти</a>", await ConfirmAsync(newToken));
    }

    private static readonly object Gate = new();
    private static WebApplicationFactory<Program>? _realCookies;

    // Второй хост на той же базе: cookie входа настоящий (в основном хосте тестов он подменён и войти нельзя).
    private WebApplicationFactory<Program> RealCookies()
    {
        lock (Gate)
            return _realCookies ??= app.WithWebHostBuilder(b =>
            {
                b.UseSetting("Jobs:Enabled", "false");
                b.ConfigureTestServices(services => services.PostConfigure<CookieAuthenticationOptions>(
                    IdentityConstants.ApplicationScheme, o => o.ForwardDefault = null));
            });
    }

    // Страница входа, как её проходит браузер: форма с защитным токеном, переходы вручную. Успех — 302 и cookie входа.
    private async Task<(bool SignedIn, string Html)> SignInPageAsync(string email, string password)
    {
        var browser = RealCookies().CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        var form = await browser.GetStringAsync("/Account/Login");
        var response = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryField().Match(form).Groups[1].Value,
            ["Email"] = email, ["Password"] = password
        }));
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                     && cookies.Any(c => c.StartsWith(".AspNetCore.Identity.Application="));
        return (response.StatusCode == HttpStatusCode.Redirect && cookie, WebHtml(await response.Content.ReadAsStringAsync()));
    }

    // Тест подтверждения выше проверяет пароль через менеджер пользователей; здесь — страница входа, которую проходит человек
    // (находка точки проверки A9: вход по новому адресу с прежним паролем не прошёл).
    [Fact]
    public async Task Sign_in_page_accepts_new_address_with_the_same_password_and_refuses_the_old_one()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        // Пароль, заданный администратором, временный (A7): вход ведёт на страницу смены. Здесь нужен обычный вход.
        await WithUsersAsync(async users =>
        {
            var account = (await users.FindByIdAsync(accountId.ToString()))!;
            account.MustChangePassword = false;
            return await users.UpdateAsync(account);
        });
        var newEmail = NewAddress();
        Assert.True((await SignInPageAsync(oldEmail, Password)).SignedIn, "До смены вход по прежнему адресу должен проходить.");
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        await ConfirmAsync(oldToken);

        Assert.True((await SignInPageAsync(oldEmail, Password)).SignedIn, "С одним подтверждением вход — по прежнему адресу.");
        Assert.False((await SignInPageAsync(newEmail, Password)).SignedIn);

        await ConfirmAsync(newToken);

        var byNew = await SignInPageAsync(newEmail, Password);
        Assert.True(byNew.SignedIn, byNew.Html);
        var byOld = await SignInPageAsync(oldEmail, Password);
        Assert.False(byOld.SignedIn);
        Assert.Contains("Неверный email или пароль", byOld.Html);
    }

    private Task<bool> EmailConfirmedAsync(Guid accountId) =>
        WithUsersAsync(async users => (await users.FindByIdAsync(accountId.ToString()))!.EmailConfirmed);

    // SetEmailAsync менеджера пользователей сбрасывает EmailConfirmed, а новый адрес подтверждён ссылкой из письма: признак
    // должен остаться (замечание точки проверки A9: после смены адреса email_confirmed = false).
    [Fact]
    public async Task Changed_address_stays_marked_as_confirmed()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        Assert.True(await EmailConfirmedAsync(accountId), "До смены адрес подтверждён.");

        await ConfirmAsync(oldToken);
        Assert.True(await EmailConfirmedAsync(accountId), "С одним подтверждением адрес прежний, и он подтверждён.");

        await ConfirmAsync(newToken);
        Assert.True(await EmailConfirmedAsync(accountId));
    }

    [Fact]
    public async Task Confirmation_order_does_not_matter()
    {
        var (client, _, oldEmail, _) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);

        Assert.Contains("Подтверждено с нового адреса", await ConfirmAsync(newToken));
        Assert.Contains("Адрес изменён", await ConfirmAsync(oldToken));

        Assert.True(await CanLogInAsync(newEmail, Password));
    }

    [Fact]
    public async Task Letters_go_to_both_addresses_and_name_each_other_without_the_other_link()
    {
        var (client, _, oldEmail, _) = await NewAccountAsync();
        var newEmail = NewAddress();

        await RequestAsync(client, newEmail);

        var toOld = Assert.Single(Mail.SentTo(oldEmail));
        var toNew = Assert.Single(Mail.SentTo(newEmail));
        Assert.Contains(newEmail, toOld.Body);
        Assert.Contains(oldEmail, toNew.Body);
        // Токен — во фрагменте адреса, а не в строке запроса: его не увидят журналы хоста и прокси.
        Assert.Matches(@"/Account/ConfirmEmail#t=[A-Za-z0-9_-]{43}\b", toOld.Body);
        Assert.DoesNotContain("ConfirmEmail?", toOld.Body);
    }

    [Fact]
    public async Task One_confirmation_leaves_address_unchanged_and_profile_shows_what_is_awaited()
    {
        var (client, employee, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, _) = Links(oldEmail, newEmail);

        await ConfirmAsync(oldToken);

        Assert.True(await CanLogInAsync(oldEmail, Password));
        Assert.Equal(oldEmail, await DirectoryEmailAsync(employee.Id));
        Assert.Equal(1, await RequestCountAsync(accountId));
        var pending = (await (await client.GetAsync("/api/profile")).Content.ReadFromJsonAsync<ProfileItem>())!.EmailChange;
        Assert.NotNull(pending);
        Assert.Equal(newEmail, pending.NewEmail);
        Assert.True(pending.OldConfirmed);
        Assert.False(pending.NewConfirmed);
    }

    [Fact]
    public async Task Repeated_click_on_a_confirmed_link_is_harmless_and_used_link_is_invalid_afterwards()
    {
        var (client, _, oldEmail, _) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);

        await ConfirmAsync(oldToken);
        Assert.Contains("Подтверждено с прежнего адреса", await ConfirmAsync(oldToken));
        Assert.Contains("Адрес изменён", await ConfirmAsync(newToken));

        Assert.Contains("Ссылка недействительна", await ConfirmAsync(oldToken));
        Assert.Contains("Ссылка недействительна", await ConfirmAsync(newToken));
    }

    [Fact]
    public async Task Expired_request_does_not_change_address_and_links_are_invalid()
    {
        var (client, employee, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        await ConfirmAsync(oldToken);
        await app.WithDbAsync(async db => await db.EmailChangeRequests.Where(r => r.UserId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        var profile = (await (await client.GetAsync("/api/profile")).Content.ReadFromJsonAsync<ProfileItem>())!;
        Assert.Null(profile.EmailChange);
        Assert.Contains("Ссылка недействительна", await ConfirmAsync(newToken));

        Assert.True(await CanLogInAsync(oldEmail, Password));
        Assert.False(await CanLogInAsync(newEmail, Password));
        Assert.Equal(oldEmail, await DirectoryEmailAsync(employee.Id));
        Assert.Equal(0, await RequestCountAsync(accountId));
    }

    [Fact]
    public async Task New_request_replaces_the_previous_one_and_its_links_stop_working()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var first = NewAddress();
        var second = NewAddress();
        await RequestAsync(client, first);
        var (firstOld, firstNew) = Links(oldEmail, first);

        Assert.Equal(HttpStatusCode.NoContent, (await RequestAsync(client, second)).StatusCode);

        Assert.Equal(1, await RequestCountAsync(accountId));
        Assert.Contains("Ссылка недействительна", await ConfirmAsync(firstOld));
        Assert.Contains("Ссылка недействительна", await ConfirmAsync(firstNew));
        var oldLetters = Mail.SentTo(oldEmail);
        Assert.Equal(2, oldLetters.Count);
        await ConfirmAsync(TokenOf(oldLetters[1]));
        Assert.Contains("Адрес изменён", await ConfirmAsync(TokenOf(Assert.Single(Mail.SentTo(second)))));
        Assert.True(await CanLogInAsync(second, Password));
        Assert.False(await CanLogInAsync(first, Password));
    }

    [Fact]
    public async Task Address_of_another_account_is_conflict_and_sends_nothing()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var (_, _, otherEmail, _) = await NewAccountAsync();

        var response = await RequestAsync(client, otherEmail.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("уже используется", await response.Content.ReadAsStringAsync());
        Assert.Empty(Mail.SentTo(oldEmail));
        Assert.Equal(0, await RequestCountAsync(accountId));
    }

    [Fact]
    public async Task Address_of_a_directory_record_without_account_is_conflict()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var signatory = NewAddress();
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(new Employee
            {
                Email = signatory, ShortName = "Подписантов П. П.", NameForDocument = "П. П. Подписантов", CanLogin = false,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var response = await RequestAsync(client, signatory);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(Mail.SentTo(oldEmail));
        Assert.Equal(0, await RequestCountAsync(accountId));
    }

    [Fact]
    public async Task Address_taken_while_waiting_for_confirmation_is_refused_and_address_stays()
    {
        var (client, employee, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        await ConfirmAsync(oldToken);
        // Адрес занимает другая запись справочника в промежутке между запросом и вторым подтверждением.
        await app.WithDbAsync(async db =>
        {
            db.Employees.Add(new Employee
            {
                Email = newEmail, ShortName = "Занятов З. З.", NameForDocument = "З. З. Занятов", CanLogin = false,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var page = await ConfirmAsync(newToken);

        Assert.Contains("занят", page);
        Assert.True(await CanLogInAsync(oldEmail, Password));
        Assert.Equal(oldEmail, await DirectoryEmailAsync(employee.Id));
        Assert.Equal(0, await RequestCountAsync(accountId));
    }

    [Fact]
    public async Task Same_malformed_or_empty_address_is_unprocessable()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();

        foreach (var bad in new[] { oldEmail, oldEmail.ToUpperInvariant(), "not-an-email", "a@b", "", "  ", null, new string('a', 250) + "@test.local" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await RequestAsync(client, bad)).StatusCode);

        Assert.Empty(Mail.SentTo(oldEmail));
        Assert.Equal(0, await RequestCountAsync(accountId));
    }

    [Fact]
    public async Task Unavailable_mail_server_gives_503_and_leaves_no_request()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        Mail.Down = true;
        try
        {
            var response = await RequestAsync(client, NewAddress());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, await RequestCountAsync(accountId));
            Assert.Empty(Mail.SentTo(oldEmail));
        }
        finally
        {
            Mail.Down = false;
        }
    }

    [Fact]
    public async Task Address_the_server_refuses_gives_422_and_the_request_is_withdrawn()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        Mail.FailFor(newEmail, new InvalidOperationException("адрес не принят"));

        var response = await RequestAsync(client, newEmail);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("сервер отказал", await response.Content.ReadAsStringAsync());
        // Письмо на прежний адрес уже ушло, но запрос снят — его ссылка не сработает.
        Assert.Equal(0, await RequestCountAsync(accountId));
        Assert.Contains("Ссылка недействительна", await ConfirmAsync(TokenOf(Assert.Single(Mail.SentTo(oldEmail)))));
    }

    [Fact]
    public async Task Disabled_account_cannot_request_and_disabling_in_between_cancels_the_change()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);
        await ConfirmAsync(oldToken);
        var admin = app.CreateClientFor(AppRoles.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{accountId}/deactivate", null)).StatusCode);

        Assert.Contains("отключена", await ConfirmAsync(newToken));
        Assert.Equal(HttpStatusCode.Conflict, (await RequestAsync(client, NewAddress())).StatusCode);
        Assert.Equal(0, await RequestCountAsync(accountId));
        Assert.Equal(oldEmail, await WithUsersAsync(async users => (await users.FindByIdAsync(accountId.ToString()))!.Email));
    }

    [Fact]
    public async Task Tokens_are_stored_only_as_hashes()
    {
        var (client, _, oldEmail, accountId) = await NewAccountAsync();
        var newEmail = NewAddress();
        await RequestAsync(client, newEmail);
        var (oldToken, newToken) = Links(oldEmail, newEmail);

        EmailChangeRequest? row = null;
        await app.WithDbAsync(async db => row = await db.EmailChangeRequests.AsNoTracking().SingleAsync(r => r.UserId == accountId));

        Assert.NotNull(row);
        Assert.DoesNotContain(oldToken, new[] { row.OldTokenHash, row.NewTokenHash });
        Assert.DoesNotContain(newToken, new[] { row.OldTokenHash, row.NewTokenHash });
        Assert.Equal(64, row.OldTokenHash.Length);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        var response = await RequestAsync(app.CreateClientFor(null), NewAddress());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Account_without_directory_record_changes_only_its_own_email()
    {
        // Учётная запись, созданная в обход справочника (так бывает у старых записей): у неё только email учётной записи.
        var email = $"nodir-{Guid.NewGuid():N}@test.local";
        var id = Guid.Empty;
        await WithUsersAsync(async users =>
        {
            var user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Без Справочника Обезличенный" };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            id = user.Id;
            return 0;
        });
        var client = app.CreateClientFor(AppRoles.Teacher, id.ToString());
        var newEmail = NewAddress();

        Assert.Equal(HttpStatusCode.NoContent, (await RequestAsync(client, newEmail)).StatusCode);
        var (oldToken, newToken) = Links(email, newEmail);
        await ConfirmAsync(oldToken);
        Assert.Contains("Адрес изменён", await ConfirmAsync(newToken));

        Assert.True(await CanLogInAsync(newEmail, Password));
    }
}
