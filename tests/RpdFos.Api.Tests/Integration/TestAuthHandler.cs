using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Тестовая схема аутентификации: роль берётся из заголовка запроса, токен не нужен. Несколько ролей — через
/// запятую: каждая становится своим утверждением role, как в токене доступа (этап 6, B1).
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
        var roles = role.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var identity = new ClaimsIdentity(
            [new Claim(Claims.Subject, subject), new Claim(Claims.Name, $"Тестовый {role}"), .. roles.Select(r => new Claim(Claims.Role, r))],
            authenticationType: SchemeName, nameType: Claims.Name, roleType: Claims.Role);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
