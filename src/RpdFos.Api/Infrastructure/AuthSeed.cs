using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
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
        var publicUrl = (config["Auth:PublicUrl"] ?? "https://localhost:5081").TrimEnd('/');
        var loginCallback = new Uri($"{publicUrl}/authentication/login-callback");
        var logoutCallback = new Uri($"{publicUrl}/authentication/logout-callback");

        var client = await apps.FindByClientIdAsync(WebClientId);
        if (client is not null)
        {
            // Клиент создаётся один раз, а адрес хоста может смениться: без синхронизации
            // вход падает с ошибкой redirect_uri. Остальные настройки клиента не трогаем.
            var current = new OpenIddictApplicationDescriptor();
            await apps.PopulateAsync(current, client);
            if (!current.RedirectUris.SetEquals([loginCallback]) ||
                !current.PostLogoutRedirectUris.SetEquals([logoutCallback]))
            {
                current.RedirectUris.Clear();
                current.RedirectUris.Add(loginCallback);
                current.PostLogoutRedirectUris.Clear();
                current.PostLogoutRedirectUris.Add(logoutCallback);
                await apps.UpdateAsync(client, current);
                logger.LogInformation("Адреса возврата клиента {ClientId} обновлены под {PublicUrl}", WebClientId, publicUrl);
            }
        }
        else
        {
            await apps.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = WebClientId,
                ClientType = ClientTypes.Public,
                ConsentType = ConsentTypes.Implicit,
                DisplayName = "АС создания РПД и ФОС",
                RedirectUris = { loginCallback },
                PostLogoutRedirectUris = { logoutCallback },
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

        // 3. Учётные записи без записи в справочнике сотрудников — созданные на этапе 1, до справочника.
        //    Без записи пользователь не может, например, загрузить исходные файлы (uploaded_by → users).
        var directory = services.GetRequiredService<DirectoryAccounts>();
        var (linked, problems) = await directory.LinkUnlinkedAccountsAsync();
        if (linked > 0)
            logger.LogInformation("Связано со справочником сотрудников учётных записей: {Count}", linked);
        foreach (var (accountEmail, problem) in problems)
            logger.LogWarning("Учётная запись {Email} не связана со справочником сотрудников: {Problem}", accountEmail, problem);

        // 4. Первый администратор — из user-secrets (в репозиторий пароль не попадает).
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Первый администратор не создан: задайте Seed:AdminEmail и Seed:AdminPassword в user-secrets.");
            return;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Администратор" };
            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded)
                throw new InvalidOperationException("Не удалось создать администратора: "
                    + string.Join("; ", created.Errors.Select(e => e.Description)));

            // Без роли администратор бесполезен, а при следующем запуске засев его пропустит
            // (учётная запись уже есть) — поэтому запись удаляется и запуск останавливается.
            var assigned = await users.AddToRoleAsync(admin, AppRoles.Admin);
            if (!assigned.Succeeded)
            {
                await users.DeleteAsync(admin);
                throw new InvalidOperationException("Не удалось назначить роль администратору: "
                    + string.Join("; ", assigned.Errors.Select(e => e.Description)));
            }
        }

        // 5. Запись администратора в справочнике сотрудников (для только что созданного).
        var (_, adminProblem) = await directory.LinkAsync(admin);
        if (adminProblem is not null)
            logger.LogWarning("Администратор не связан со справочником сотрудников: {Problem}", adminProblem);
    }
}
