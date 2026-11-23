using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using RpdFos.Api.Features.Profile;
using RpdFos.Api.Features.Users;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data.Entities;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Смена своего пароля и временный пароль (этап 8, A7). Профиль — запросами API от имени учётной записи; вход, страница
/// смены и точка авторизации — на втором хосте с настоящим cookie входа (в основном хосте тестов он подменён и войти
/// нельзя), как их проходит браузер: страницы с защитным токеном, переходы вручную.
/// </summary>
[Collection(DatabaseCollection.Name)]
public partial class TemporaryPasswordTests(DatabaseFixture app)
{
    private const string Temporary = "Temp-Passw0rd";
    private const string Chosen = "Chosen-Passw0rd!";
    private const string Callback = "https://localhost/authentication/login-callback";
    private const string Verifier = "0123456789012345678901234567890123456789012";

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private static readonly object Gate = new();
    private static WebApplicationFactory<Program>? _realCookies;

    // Второй хост на той же базе: cookie входа настоящий. Сервера фоновых задач в нём нет — он нужен одному хосту.
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

    // «Вкладка» браузера: свои cookie, переходы не выполняются сами — их видно в тесте.
    private HttpClient Browser() => RealCookies().CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient browser, string page, Dictionary<string, string> fields)
    {
        var form = await browser.GetStringAsync(page);
        fields["__RequestVerificationToken"] = AntiforgeryField().Match(form).Groups[1].Value;
        return await browser.PostAsync(page, new FormUrlEncodedContent(fields));
    }

    private static async Task<string> HtmlAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static bool HasSignInCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(".AspNetCore.Identity.Application="));

    private async Task<(string Email, Guid Id)> NewAccountAsync(string? password = Temporary)
    {
        var email = $"temp-{Guid.NewGuid():N}@test.local";
        var created = await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest(email, "Получатель Пароля Обезличенный", password, [AppRoles.Teacher]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (email, (await created.Content.ReadFromJsonAsync<UserItem>())!.Id);
    }

    private async Task<T> WithUsersAsync<T>(Func<UserManager<AppUser>, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private Task<bool> MustChangeAsync(Guid id) => WithUsersAsync(async users => (await users.FindByIdAsync(id.ToString()))!.MustChangePassword);

    private Task<bool> CanLogInAsync(string email, string password) => WithUsersAsync(async users =>
        await users.FindByEmailAsync(email) is { } user && await users.CheckPasswordAsync(user, password));

    // Запись, у которой временный пароль уже сменён: нужна, чтобы открыть сеанс до того, как администратор задаст новый.
    private Task ClearMustChangeAsync(Guid id) => WithUsersAsync(async users =>
    {
        var user = (await users.FindByIdAsync(id.ToString()))!;
        user.MustChangePassword = false;
        return await users.UpdateAsync(user);
    });

    private HttpClient Profile(Guid id) => app.CreateClientFor(AppRoles.Teacher, id.ToString());

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string? current, string? next) =>
        client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(current, next));

    // ----- Признак -----

    [Fact]
    public async Task Password_set_by_admin_is_temporary_at_creation_and_with_the_button_but_not_without_password()
    {
        var admin = app.CreateClientFor(AppRoles.Admin);
        var (_, withPassword) = await NewAccountAsync();
        var (_, withoutPassword) = await NewAccountAsync(password: null);
        var (_, later) = await NewAccountAsync(password: null);

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/admin/users/{later}/password", new PasswordRequest(Temporary))).StatusCode);

        Assert.True(await MustChangeAsync(withPassword));
        Assert.False(await MustChangeAsync(withoutPassword));
        Assert.True(await MustChangeAsync(later));
        var listed = (await admin.GetFromJsonAsync<List<AccountItem>>("/api/admin/users"))!;
        Assert.True(listed.Single(a => a.Id == withPassword).MustChangePassword);
        Assert.False(listed.Single(a => a.Id == withoutPassword).MustChangePassword);
    }

    // ----- Своя смена пароля в профиле -----

    [Fact]
    public async Task Profile_password_change_needs_the_right_current_password()
    {
        var (email, id) = await NewAccountAsync();

        var response = await ChangeAsync(Profile(id), "Wrong-Passw0rd!", Chosen);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Текущий пароль указан неверно.", await response.Content.ReadAsStringAsync());
        Assert.True(await CanLogInAsync(email, Temporary));
        Assert.False(await CanLogInAsync(email, Chosen));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ChangeAsync(Profile(id), null, Chosen)).StatusCode);
    }

    [Fact]
    public async Task Profile_password_change_works_and_clears_the_temporary_mark()
    {
        var (email, id) = await NewAccountAsync();
        Assert.True(await MustChangeAsync(id));

        var response = await ChangeAsync(Profile(id), Temporary, Chosen);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await CanLogInAsync(email, Chosen));
        Assert.False(await CanLogInAsync(email, Temporary));
        Assert.False(await MustChangeAsync(id));
    }

    [Fact]
    public async Task Profile_password_change_explains_a_weak_or_unchanged_new_password_and_changes_nothing()
    {
        var (email, id) = await NewAccountAsync();

        var weak = await ChangeAsync(Profile(id), Temporary, "Aa1!");
        var same = await ChangeAsync(Profile(id), Temporary, Temporary);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        Assert.Contains("Пароль не короче 8 символов.", await weak.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, same.StatusCode);
        Assert.Contains("отличаться", await same.Content.ReadAsStringAsync());
        Assert.True(await CanLogInAsync(email, Temporary));
        Assert.True(await MustChangeAsync(id));
    }

    [Fact]
    public async Task Profile_password_change_counts_wrong_current_passwords_and_stops_at_the_lockout()
    {
        var (email, id) = await NewAccountAsync();
        for (var i = 0; i < 5; i++)
            await ChangeAsync(Profile(id), "Wrong-Passw0rd!", Chosen);

        // Блокировка общая со входом: верный текущий пароль после пяти неудач уже не принимается.
        var response = await ChangeAsync(Profile(id), Temporary, Chosen);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("заблокирована", await response.Content.ReadAsStringAsync());
        Assert.False(await CanLogInAsync(email, Chosen));
    }

    [Fact]
    public async Task Profile_password_change_answers_for_unknown_account_and_anonymous()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(Profile(Guid.NewGuid()), Temporary, Chosen)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(app.CreateClientFor(null), Temporary, Chosen)).StatusCode);
    }

    [Fact]
    public async Task Recovery_link_clears_the_temporary_mark()
    {
        var (email, id) = await NewAccountAsync();
        Assert.True(await MustChangeAsync(id));
        await using var scope = app.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<RpdFos.Core.Accounts.PasswordLinks>();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
        var token = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().GeneratePasswordResetTokenAsync(user!);

        var result = await links.SetPasswordAsync(id.ToString("N"), token, Chosen);

        Assert.True(result.Succeeded);
        Assert.False(await MustChangeAsync(id));
        Assert.True(await CanLogInAsync(email, Chosen));
    }

    // ----- Вход -----

    [Fact]
    public async Task Sign_in_with_temporary_password_leads_to_the_change_page_without_opening_a_session()
    {
        var (email, _) = await NewAccountAsync();
        var browser = Browser();

        var response = await SubmitAsync(browser, "/Account/Login?ReturnUrl=%2Fconnect%2Fauthorize%3Fx%3D1",
            new() { ["Email"] = email, ["Password"] = Temporary, ["ReturnUrl"] = "/connect/authorize?x=1" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/Account/ChangePassword?", location);
        Assert.Contains("ReturnUrl=", location);
        Assert.False(HasSignInCookie(response), "До смены пароля сеанс входа открываться не должен.");
    }

    [Fact]
    public async Task Wrong_password_for_a_temporary_account_is_an_ordinary_failure_with_counting()
    {
        var (email, _) = await NewAccountAsync();
        var browser = Browser();

        var response = await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = "Wrong-Passw0rd!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Неверный email или пароль.", await HtmlAsync(response));
        Assert.Equal(1, await WithUsersAsync(async users => await users.GetAccessFailedCountAsync((await users.FindByEmailAsync(email))!)));

        for (var i = 0; i < 4; i++)
            await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = "Wrong-Passw0rd!" });
        var locked = await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = Temporary });
        Assert.Contains("временно заблокирована", await HtmlAsync(locked));
    }

    [Fact]
    public async Task Change_page_sets_own_password_signs_in_and_returns_to_the_request()
    {
        var (email, id) = await NewAccountAsync();
        var browser = Browser();

        var response = await SubmitAsync(browser, "/Account/ChangePassword", new()
        {
            ["Email"] = email, ["CurrentPassword"] = Temporary, ["NewPassword"] = Chosen, ["Confirm"] = Chosen,
            ["ReturnUrl"] = "/connect/authorize?x=1"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/connect/authorize?x=1", response.Headers.Location!.OriginalString);
        Assert.True(HasSignInCookie(response));
        Assert.False(await MustChangeAsync(id));
        Assert.True(await CanLogInAsync(email, Chosen));
        Assert.False(await CanLogInAsync(email, Temporary));
    }

    [Fact]
    public async Task Change_page_refuses_wrong_temporary_password_mismatch_weak_and_unchanged_passwords()
    {
        var (email, id) = await NewAccountAsync();
        var browser = Browser();
        async Task<string> TryAsync(string address, string current, string next, string? confirm = null)
        {
            var response = await SubmitAsync(browser, "/Account/ChangePassword", new()
            {
                ["Email"] = address, ["CurrentPassword"] = current, ["NewPassword"] = next, ["Confirm"] = confirm ?? next
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(HasSignInCookie(response));
            return await HtmlAsync(response);
        }

        Assert.Contains("Неверный email или временный пароль.", await TryAsync(email, "Wrong-Passw0rd!", Chosen));
        // Неизвестный адрес отвечает так же: страница не выдаёт, есть ли такая запись.
        Assert.Contains("Неверный email или временный пароль.", await TryAsync($"nobody-{Guid.NewGuid():N}@test.local", Temporary, Chosen));
        Assert.Contains("Пароли не совпадают.", await TryAsync(email, Temporary, Chosen, "Other-Passw0rd!"));
        Assert.Contains("Пароль не короче 8 символов.", await TryAsync(email, Temporary, "Aa1!"));
        Assert.Contains("отличаться", await TryAsync(email, Temporary, Temporary));

        Assert.True(await MustChangeAsync(id));
        Assert.True(await CanLogInAsync(email, Temporary));
    }

    // ----- Точка авторизации -----

    [Fact]
    public async Task Authorize_with_an_open_session_returns_to_the_change_page_after_admin_sets_a_temporary_password()
    {
        await EnsureClientAsync();
        var (email, id) = await NewAccountAsync();
        await ClearMustChangeAsync(id);
        var browser = Browser();
        var login = await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = Temporary });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.True(HasSignInCookie(login));
        // Сеанс до смены пароля пускает: код выдаётся.
        Assert.Contains("code=", (await browser.GetAsync(AuthorizeUrl())).Headers.Location!.OriginalString);

        // Администратор задаёт новый пароль, а сеанс остался открытым (штамп проверяется раз в полчаса).
        Assert.Equal(HttpStatusCode.NoContent, (await app.CreateClientFor(AppRoles.Admin)
            .PostAsJsonAsync($"/api/admin/users/{id}/password", new PasswordRequest("Admin-Set-Passw0rd"))).StatusCode);
        var response = await browser.GetAsync(AuthorizeUrl());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/Account/ChangePassword?", location);
        Assert.Contains("ReturnUrl=" + Uri.EscapeDataString("/connect/authorize?"), location);
        Assert.Contains("Email=" + Uri.EscapeDataString(email), location);
        // Сеанс закрыт: следующий запрос без смены пароля снова приходит на страницу входа или смены, а не выдаёт код.
        var again = await browser.GetAsync(AuthorizeUrl());
        Assert.DoesNotContain("code=", again.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Silent_sign_in_with_a_temporary_password_session_answers_login_required()
    {
        await EnsureClientAsync();
        var (email, id) = await NewAccountAsync();
        await ClearMustChangeAsync(id);
        var browser = Browser();
        await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = Temporary });
        await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync($"/api/admin/users/{id}/password", new PasswordRequest("Admin-Set-Passw0rd"));

        var response = await browser.GetAsync(AuthorizeUrl("&prompt=none"));

        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith(Callback, location);
        Assert.Contains("error=login_required", location);
    }

    [Fact]
    public async Task Code_issued_before_the_temporary_password_is_not_exchanged_for_tokens()
    {
        await EnsureClientAsync();
        var (email, id) = await NewAccountAsync();
        await ClearMustChangeAsync(id);
        var browser = Browser();
        await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = Temporary });
        var code = Regex.Match((await browser.GetAsync(AuthorizeUrl())).Headers.Location!.OriginalString, "code=([^&]+)").Groups[1].Value;
        Assert.NotEmpty(code);
        await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync($"/api/admin/users/{id}/password", new PasswordRequest("Admin-Set-Passw0rd"));

        var token = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = AuthSeed.WebClientId, ["code"] = code,
            ["redirect_uri"] = Callback, ["code_verifier"] = Verifier
        }));

        Assert.Equal(HttpStatusCode.BadRequest, token.StatusCode);
        Assert.Contains("invalid_grant", await token.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task After_the_change_the_whole_circuit_issues_the_code()
    {
        await EnsureClientAsync();
        var (email, _) = await NewAccountAsync();
        var browser = Browser();
        var authorize = AuthorizeUrl();

        // Пользователь не вошёл: authorize отправляет на вход, вход — на смену пароля, смена — обратно на authorize.
        var challenge = await browser.GetAsync(authorize);
        Assert.Contains("/Account/Login", challenge.Headers.Location!.OriginalString);
        var login = await SubmitAsync(browser, "/Account/Login", new() { ["Email"] = email, ["Password"] = Temporary, ["ReturnUrl"] = authorize });
        Assert.StartsWith("/Account/ChangePassword?", login.Headers.Location!.OriginalString);
        var change = await SubmitAsync(browser, "/Account/ChangePassword", new()
        {
            ["Email"] = email, ["CurrentPassword"] = Temporary, ["NewPassword"] = Chosen, ["Confirm"] = Chosen, ["ReturnUrl"] = authorize
        });
        Assert.Equal(authorize, change.Headers.Location!.OriginalString);

        var issued = await browser.GetAsync(authorize);

        Assert.StartsWith(Callback, issued.Headers.Location!.OriginalString);
        Assert.Contains("code=", issued.Headers.Location.OriginalString);
    }

    private static string AuthorizeUrl(string extra = "")
    {
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "/connect/authorize?client_id=" + AuthSeed.WebClientId + "&redirect_uri=" + Uri.EscapeDataString(Callback) +
               "&response_type=code&scope=openid%20profile%20roles&state=s1&code_challenge=" + challenge +
               "&code_challenge_method=S256&response_mode=query" + extra;
    }

    private async Task EnsureClientAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        if (await apps.FindByClientIdAsync(AuthSeed.WebClientId) is not null) return;
        await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = AuthSeed.WebClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri(Callback) },
            Permissions =
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code, Permissions.Scopes.Profile, Permissions.Scopes.Roles
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });
    }
}
