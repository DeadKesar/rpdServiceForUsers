using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Тестовая схема аутентификации: роль берётся из заголовка запроса, токен не нужен.
/// Подменяется только способ входа — политики доступа остаются рабочими.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string RoleHeader = "X-Test-Role";
    public const string SubjectHeader = "X-Test-Subject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Без заголовка — анонимный запрос: API ответит 401, как без токена.
        if (!Request.Headers.TryGetValue(RoleHeader, out var role) || string.IsNullOrEmpty(role))
            return Task.FromResult(AuthenticateResult.NoResult());

        // Утверждения — как в токене доступа OpenIddict: sub, name, role. sub можно задать заголовком —
        // чтобы связать запрос с записью справочника сотрудников.
        var subject = Request.Headers.TryGetValue(SubjectHeader, out var s) && !string.IsNullOrEmpty(s)
            ? s.ToString()
            : Guid.NewGuid().ToString();
        var identity = new ClaimsIdentity(
            [
                new Claim(Claims.Subject, subject),
                new Claim(Claims.Name, $"Тестовый {role}"),
                new Claim(Claims.Role, role.ToString())
            ],
            authenticationType: SchemeName, nameType: Claims.Name, roleType: Claims.Role);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
