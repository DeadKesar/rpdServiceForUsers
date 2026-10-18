using System.Security.Claims;
using Carter;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using RpdFos.Core.Data.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Features.Auth;

/// <summary>
/// Конечные точки OpenID Connect сервера авторизации OpenIddict (внутри хоста API).
/// По образцу официального примера OpenIddict «Balosar», упрощено для собственного клиента:
/// без экрана согласия и без внешних провайдеров входа.
/// </summary>
public class ConnectModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], Authorize).ExcludeFromDescription();
        app.MapPost("/connect/token", Exchange).ExcludeFromDescription();
        app.MapMethods("/connect/logout", ["GET", "POST"], Logout).ExcludeFromDescription();
    }

    // Запрос кода авторизации. Пользователь не вошёл — отправляем на страницу входа
    // и возвращаемся сюда же; вошёл — выдаём код (экран согласия не нужен: клиент свой).
    private static async Task<IResult> Authorize(HttpContext http, UserManager<AppUser> users)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Запрос OpenID Connect не найден.");

        var auth = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!auth.Succeeded)
            return Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = http.Request.PathBase + http.Request.Path + http.Request.QueryString
                },
                [IdentityConstants.ApplicationScheme]);

        var user = await users.GetUserAsync(auth.Principal!)
            ?? throw new InvalidOperationException("Пользователь не найден.");

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        await SetUserClaimsAsync(identity, users, user);

        identity.SetScopes(request.GetScopes());
        identity.SetDestinations(GetDestinations);

        return Results.SignIn(new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // Обмен кода авторизации (или refresh-токена) на токены доступа и идентификации.
    private static async Task<IResult> Exchange(
        HttpContext http, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Запрос OpenID Connect не найден.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            throw new InvalidOperationException("Этот тип гранта не поддерживается.");

        // Данные, сохранённые в коде авторизации или refresh-токене.
        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var user = await users.FindByIdAsync(result.Principal!.GetClaim(Claims.Subject)!);
        if (user is null || !await signIn.CanSignInAsync(user))
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                        "Учётная запись недоступна для входа."
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

        // Свежие данные пользователя поверх сохранённых: роль или ФИО могли измениться.
        var identity = new ClaimsIdentity(result.Principal!.Claims,
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        await SetUserClaimsAsync(identity, users, user);
        identity.SetDestinations(GetDestinations);

        return Results.SignIn(new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // Выход: закрыть сессию входа и вернуть пользователя в веб-приложение.
    private static async Task<IResult> Logout(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static async Task SetUserClaimsAsync(ClaimsIdentity identity, UserManager<AppUser> users, AppUser user)
    {
        identity.SetClaim(Claims.Subject, await users.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await users.GetEmailAsync(user))
                .SetClaim(Claims.Name, user.FullName)
                .SetClaim(Claims.PreferredUsername, await users.GetUserNameAsync(user))
                .SetClaims(Claims.Role, [.. await users.GetRolesAsync(user)]);
    }

    // В какие токены попадает каждое утверждение: по умолчанию — никуда.
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Email))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Roles))
                    yield return Destinations.IdentityToken;
                yield break;

            // Штамп безопасности — секрет, в токены не попадает.
            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
