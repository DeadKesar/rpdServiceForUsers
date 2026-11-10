using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication.Internal;

namespace RpdFos.Web.Auth;

/// <summary>
/// Несколько ролей у учётной записи (этап 6, B1). Сервер выдаёт их утверждением <c>role</c> — массивом, а стандартная
/// фабрика Blazor кладёт массив одним утверждением с текстом JSON: <c>["teacher","approver"]</c>. Проверки ролей в
/// меню и на страницах (<c>IsInRole</c>) такого значения не понимают, поэтому массив разбирается на отдельные
/// утверждения. Одна роль приходит строкой — она остаётся как есть.
/// </summary>
public sealed class RolesClaimsPrincipalFactory(IAccessTokenProviderAccessor accessor)
    : AccountClaimsPrincipalFactory<RemoteUserAccount>(accessor)
{
    public override async ValueTask<ClaimsPrincipal> CreateUserAsync(RemoteUserAccount account, RemoteAuthenticationUserOptions options)
    {
        var user = await base.CreateUserAsync(account, options);
        if (user.Identity is not ClaimsIdentity { IsAuthenticated: true } identity) return user;

        foreach (var claim in identity.FindAll(identity.RoleClaimType).ToList())
        {
            if (Roles(claim.Value) is not { } roles) continue;
            identity.RemoveClaim(claim);
            identity.AddClaims(roles.Select(r => new Claim(identity.RoleClaimType, r)));
        }
        return user;
    }

    // Значение-массив — роли по отдельности; обычная строка — null: менять нечего.
    private static List<string>? Roles(string value)
    {
        if (!value.TrimStart().StartsWith('[')) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
