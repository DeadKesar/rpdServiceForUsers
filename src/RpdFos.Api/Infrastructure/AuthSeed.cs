using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using RpdFos.Core.Data.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Начальные данные аутентификации: роли, клиент OIDC веб-приложения, первый администратор.
/// Идемпотентно: при повторном запуске ничего не дублируется.
/// </summary>
public static class AuthSeed
{
    public const string WebClientId = "rpdfos-web";

    public static async Task RunAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        // 1. Роли
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in AppRoles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));

        // 2. Клиент OIDC — веб-приложение: публичный клиент, authorization code + PKCE, без экрана согласия.
        var apps = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await apps.FindByClientIdAsync(WebClientId) is null)
        {
            var publicUrl = (config["Auth:PublicUrl"] ?? "https://localhost:5081").TrimEnd('/');
            await apps.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = WebClientId,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Implicit,
                DisplayName = "АС создания РПД и ФОС",
                RedirectUris = { new Uri($"{publicUrl}/authentication/login-callback") },
                PostLogoutRedirectUris = { new Uri($"{publicUrl}/authentication/logout-callback") },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.EndSession,
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email,
                    Permissions.Scopes.Profile,
                    Permissions.Scopes.Roles
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            });
        }

        // 3. Первый администратор — из user-secrets (в репозиторий пароль не попадает).
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Первый администратор не создан: задайте Seed:AdminEmail и Seed:AdminPassword в user-secrets.");
            return;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync(email) is null)
        {
            var admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Администратор" };
            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded)
                throw new InvalidOperationException("Не удалось создать администратора: "
                    + string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(admin, AppRoles.Admin);
        }
    }
}
