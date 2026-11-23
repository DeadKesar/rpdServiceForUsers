using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RpdFos.Api.Features.Users;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Mail;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Восстановление пароля и приглашение (этап 8, A6): страницы «Забыли пароль?» и «Новый пароль» проходятся как в жизни —
/// запросом формы с защитным токеном, ссылка берётся из письма, которое поймал <see cref="FakeMailSender"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public partial class PasswordLinksTests(DatabaseFixture app)
{
    private const string Password = "Test-Passw0rd";
    private const string NewPassword = "Brand-New-Passw0rd!";
    private const string Answer = "Если адрес зарегистрирован, письмо со ссылкой для нового пароля отправлено.";

    private FakeMailSender Mail => app.Services.GetRequiredService<FakeMailSender>();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    [GeneratedRegex(@"/Account/SetPassword#u=([0-9a-f]{32})&t=(\S+)")]
    private static partial Regex LinkInLetter();

    // Страница отдаёт форму с защитным токеном и cookie; клиент с cookie — свой на каждую «вкладку».
    private HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpStatusCode Status, string Html)> SubmitAsync(HttpClient browser, string page, Dictionary<string, string> fields)
    {
        var form = await browser.GetStringAsync(page);
        fields["__RequestVerificationToken"] = AntiforgeryField().Match(form).Groups[1].Value;
        var response = await browser.PostAsync(page, new FormUrlEncodedContent(fields));
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    private async Task<string> ForgotAsync(string email)
    {
        var (status, html) = await SubmitAsync(Browser(), "/Account/ForgotPassword", new() { ["Email"] = email });
        Assert.Equal(HttpStatusCode.OK, status);
        return html;
    }

    // Ссылка из письма: идентификатор и токен — во фрагменте, как их читает страница скриптом.
    private static (string UserId, string Token) LinkOf(OutgoingMail letter)
    {
        var match = LinkInLetter().Match(letter.Body);
        Assert.True(match.Success, "В письме нет ссылки на страницу «Новый пароль».");
        return (match.Groups[1].Value, Uri.UnescapeDataString(match.Groups[2].Value));
    }

    private Task<(HttpStatusCode Status, string Html)> SetPasswordAsync((string UserId, string Token) link, string password, string? confirm = null) =>
        SubmitAsync(Browser(), "/Account/SetPassword", new()
        {
            ["UserId"] = link.UserId, ["Token"] = link.Token, ["Password"] = password, ["Confirm"] = confirm ?? password
        });

    private async Task<(string Email, Guid Id)> NewAccountAsync(string? password = Password, string name = "Получатель Писем Обезличенный")
    {
        var email = $"link-{Guid.NewGuid():N}@test.local";
        var created = await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest(email, name, password, [AppRoles.Teacher]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (email, (await created.Content.ReadFromJsonAsync<UserItem>())!.Id);
    }

    private async Task<T> WithUsersAsync<T>(Func<UserManager<AppUser>, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private Task<bool> CanLogInAsync(string email, string password) => WithUsersAsync(async users =>
        await users.FindByEmailAsync(email) is { } user && await users.CheckPasswordAsync(user, password));

    private Task<bool> HasPasswordAsync(Guid id) =>
        WithUsersAsync(async users => await users.HasPasswordAsync((await users.FindByIdAsync(id.ToString()))!));

    [Fact]
    public async Task Reset_link_sets_new_password_and_old_one_stops_working()
    {
        var (email, _) = await NewAccountAsync();

        Assert.Equal(Answer, Regex.Match(await ForgotAsync(email), Regex.Escape(Answer)).Value);
        var letter = Assert.Single(Mail.SentTo(email));
        Assert.Contains("Восстановление пароля", letter.Subject);

        var (status, html) = await SetPasswordAsync(LinkOf(letter), NewPassword);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Пароль задан", html);
        Assert.True(await CanLogInAsync(email, NewPassword));
        Assert.False(await CanLogInAsync(email, Password));
    }

    [Fact]
    public async Task Link_keeps_token_in_fragment_so_it_never_reaches_a_request_line()
    {
        var (email, _) = await NewAccountAsync();
        await ForgotAsync(email);

        var link = new Uri(Regex.Match(Assert.Single(Mail.SentTo(email)).Body, @"https://\S+").Value);

        Assert.Equal("https://rpdfos.test/Account/SetPassword", link.GetLeftPart(UriPartial.Path));
        Assert.Equal("", link.Query);
        Assert.StartsWith("#u=", link.Fragment);
    }

    [Fact]
    public async Task Unknown_disabled_and_known_addresses_get_the_same_answer_and_only_active_one_gets_a_letter()
    {
        var (known, _) = await NewAccountAsync();
        var (disabled, disabledId) = await NewAccountAsync();
        var unknown = $"nobody-{Guid.NewGuid():N}@test.local";
        Assert.Equal(HttpStatusCode.NoContent,
            (await app.CreateClientFor(AppRoles.Admin).PostAsync($"/api/admin/users/{disabledId}/deactivate", null)).StatusCode);

        var forKnown = await ForgotAsync(known);
        var forUnknown = await ForgotAsync(unknown);
        var forDisabled = await ForgotAsync(disabled);

        Assert.Equal(forKnown, forUnknown);
        Assert.Equal(forKnown, forDisabled);
        Assert.Contains(Answer, forKnown);
        Assert.Single(Mail.SentTo(known));
        Assert.Empty(Mail.SentTo(unknown));
        Assert.Empty(Mail.SentTo(disabled));
    }

    [Fact]
    public async Task Mail_server_failure_does_not_change_the_answer()
    {
        var (email, _) = await NewAccountAsync();
        var normal = await ForgotAsync(email);

        Mail.Down = true;
        try
        {
            Assert.Equal(normal, await ForgotAsync(email));
        }
        finally
        {
            Mail.Down = false;
        }
    }

    [Fact]
    public async Task Link_works_once_and_other_links_issued_before_stop_working()
    {
        var (email, _) = await NewAccountAsync();
        await ForgotAsync(email);
        await ForgotAsync(email);
        var letters = Mail.SentTo(email);
        Assert.Equal(2, letters.Count);

        Assert.Contains("Пароль задан", (await SetPasswordAsync(LinkOf(letters[1]), NewPassword)).Html);

        // Той же ссылкой повторно и прежней ссылкой — отказ: смена пароля обновила штамп безопасности.
        foreach (var letter in letters)
        {
            var (_, html) = await SetPasswordAsync(LinkOf(letter), "Another-Passw0rd!");
            Assert.Contains("Ссылка недействительна", html);
        }
        Assert.True(await CanLogInAsync(email, NewPassword));
    }

    [Fact]
    public async Task Expired_link_is_rejected()
    {
        var (email, _) = await NewAccountAsync();
        await ForgotAsync(email);
        var link = LinkOf(Assert.Single(Mail.SentTo(email)));

        var options = app.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        var lifespan = options.TokenLifespan;
        options.TokenLifespan = TimeSpan.FromMinutes(-1); // срок уже вышел
        try
        {
            var (_, html) = await SetPasswordAsync(link, NewPassword);
            Assert.Contains("Ссылка недействительна", html);
        }
        finally
        {
            options.TokenLifespan = lifespan;
        }
        Assert.True(await CanLogInAsync(email, Password));
        // Срок вышел, а не ссылка испорчена: вернув срок, ею можно воспользоваться.
        Assert.Contains("Пароль задан", (await SetPasswordAsync(link, NewPassword)).Html);
    }

    [Fact]
    public void Invitation_lives_longer_than_reset_link()
    {
        var reset = app.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan;
        var invitation = app.Services.GetRequiredService<IOptions<InvitationTokenProviderOptions>>().Value.TokenLifespan;

        Assert.Equal(TimeSpan.FromHours(24), reset);
        Assert.Equal(TimeSpan.FromHours(72), invitation);
    }

    [Fact]
    public async Task Weak_password_is_refused_in_russian_and_the_link_stays_valid()
    {
        var (email, _) = await NewAccountAsync();
        await ForgotAsync(email);
        var link = LinkOf(Assert.Single(Mail.SentTo(email)));

        var (_, tooShort) = await SetPasswordAsync(link, "Aa1!");
        Assert.Contains("Пароль не короче 8 символов.", tooShort);
        var (_, mismatch) = await SetPasswordAsync(link, NewPassword, "Другой-Passw0rd!");
        Assert.Contains("Пароли не совпадают.", mismatch);
        Assert.True(await CanLogInAsync(email, Password));

        Assert.Contains("Пароль задан", (await SetPasswordAsync(link, NewPassword)).Html);
    }

    [Fact]
    public async Task Reset_removes_the_lockout_after_failed_sign_ins()
    {
        var (email, _) = await NewAccountAsync();
        await WithUsersAsync(async users =>
        {
            var user = (await users.FindByEmailAsync(email))!;
            await users.SetLockoutEnabledAsync(user, true);
            for (var i = 0; i < 5; i++) await users.AccessFailedAsync(user);
            Assert.True(await users.IsLockedOutAsync(user));
            return 0;
        });

        await ForgotAsync(email);
        Assert.Contains("Пароль задан", (await SetPasswordAsync(LinkOf(Assert.Single(Mail.SentTo(email))), NewPassword)).Html);

        await WithUsersAsync(async users =>
        {
            var user = (await users.FindByEmailAsync(email))!;
            Assert.False(await users.IsLockedOutAsync(user));
            Assert.Equal(0, await users.GetAccessFailedCountAsync(user));
            return 0;
        });
    }

    [Fact]
    public async Task Account_without_password_cannot_sign_in_until_invited_and_invitation_sets_the_password()
    {
        var (email, id) = await NewAccountAsync(password: null);
        Assert.False(await HasPasswordAsync(id));
        Assert.False(await CanLogInAsync(email, Password));
        var listed = (await app.CreateClientFor(AppRoles.Admin).GetFromJsonAsync<List<AccountItem>>("/api/admin/users"))!;
        Assert.False(listed.Single(a => a.Id == id).HasPassword);

        var sent = await app.CreateClientFor(AppRoles.Admin).PostAsync($"/api/admin/users/{id}/invitation", null);

        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        var letter = Assert.Single(Mail.SentTo(email));
        Assert.Contains("Приглашение", letter.Subject);
        Assert.Contains(email, letter.Body);
        Assert.Contains("Пароль задан", (await SetPasswordAsync(LinkOf(letter), NewPassword)).Html);

        Assert.True(await CanLogInAsync(email, NewPassword));
        Assert.True(await HasPasswordAsync(id));
        // Приглашение одноразовое.
        Assert.Contains("Ссылка недействительна", (await SetPasswordAsync(LinkOf(letter), "Another-Passw0rd!")).Html);
    }

    [Fact]
    public async Task Reset_letter_works_for_an_account_without_password_and_kills_the_invitation()
    {
        var (email, id) = await NewAccountAsync(password: null);
        await app.CreateClientFor(AppRoles.Admin).PostAsync($"/api/admin/users/{id}/invitation", null);
        await ForgotAsync(email);
        var letters = Mail.SentTo(email);
        Assert.Equal(2, letters.Count);

        // Письмо восстановления отработало для записи без пароля; приглашение после него уже недействительно.
        Assert.Contains("Пароль задан", (await SetPasswordAsync(LinkOf(letters[1]), NewPassword)).Html);
        Assert.Contains("Ссылка недействительна", (await SetPasswordAsync(LinkOf(letters[0]), "Another-Passw0rd!")).Html);
    }

    [Fact]
    public async Task Invitation_dies_when_the_account_is_disabled_and_returned()
    {
        var (email, id) = await NewAccountAsync(password: null);
        var admin = app.CreateClientFor(AppRoles.Admin);
        await admin.PostAsync($"/api/admin/users/{id}/invitation", null);
        var link = LinkOf(Assert.Single(Mail.SentTo(email)));

        await admin.PostAsync($"/api/admin/users/{id}/deactivate", null);
        Assert.Contains("Ссылка недействительна", (await SetPasswordAsync(link, NewPassword)).Html);
        await admin.PostAsync($"/api/admin/users/{id}/activate", null);

        // Отключение обновляет штамп безопасности: письмо, выданное до него, пароля не даст.
        Assert.Contains("Ссылка недействительна", (await SetPasswordAsync(link, NewPassword)).Html);
        Assert.False(await HasPasswordAsync(id));
    }

    [Fact]
    public async Task Invitation_endpoint_answers_for_each_problem()
    {
        var admin = app.CreateClientFor(AppRoles.Admin);
        var (email, id) = await NewAccountAsync(password: null);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/users/{Guid.NewGuid()}/invitation", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.CreateClientFor(AppRoles.Methodist).PostAsync($"/api/admin/users/{id}/invitation", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateClientFor(null).PostAsync($"/api/admin/users/{id}/invitation", null)).StatusCode);

        // Сервер не принял адрес, сервер недоступен.
        Mail.FailFor(email, new InvalidOperationException("адрес не принят"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync($"/api/admin/users/{id}/invitation", null)).StatusCode);
        Mail.Recover(email);
        Mail.Down = true;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsync($"/api/admin/users/{id}/invitation", null)).StatusCode);
        }
        finally
        {
            Mail.Down = false;
        }

        // Не задан адрес системы: письму не из чего собрать ссылку.
        var mail = app.Services.GetRequiredService<IOptions<MailSettings>>().Value;
        var publicUrl = mail.PublicUrl;
        mail.PublicUrl = "";
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/users/{id}/invitation", null)).StatusCode);
        }
        finally
        {
            mail.PublicUrl = publicUrl;
        }

        // Отключённой записи приглашение не отправляется.
        await admin.PostAsync($"/api/admin/users/{id}/deactivate", null);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/users/{id}/invitation", null)).StatusCode);
        Assert.Empty(Mail.SentTo(email));
    }

    [Fact]
    public async Task Weak_password_at_account_creation_is_explained_in_russian()
    {
        var response = await app.CreateClientFor(AppRoles.Admin).PostAsJsonAsync("/api/admin/users",
            new CreateUserRequest($"weak-{Guid.NewGuid():N}@test.local", "Получатель Писем Обезличенный", "abc", [AppRoles.Teacher]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Пароль не короче 8 символов.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_page_offers_the_forgot_password_link()
    {
        var html = await Browser().GetStringAsync("/Account/Login");

        Assert.Contains("href=\"/Account/ForgotPassword\"", html);
        Assert.Contains("Забыли пароль?", html);
    }

    [Fact]
    public async Task Set_password_page_without_a_link_refuses_the_form()
    {
        var (_, html) = await SetPasswordAsync((Guid.NewGuid().ToString("N"), "not-a-token"), NewPassword);

        Assert.Contains("Ссылка недействительна", html);
    }
}
