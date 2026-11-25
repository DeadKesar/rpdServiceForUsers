using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Засев клиента OIDC (этап 8, B4): адреса возврата берутся из <c>Auth:PublicUrl</c>, при смене адреса — обновляются.</summary>
[Collection(DatabaseCollection.Name)]
public class AuthSeedTests(DatabaseFixture app)
{
    private async Task<(HashSet<Uri> Login, HashSet<Uri> Logout)> SeedAsync(string publicUrl)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:PublicUrl"] = publicUrl }).Build();
        await using var scope = app.Services.CreateAsyncScope();
        await AuthSeed.RunAsync(scope.ServiceProvider, config, NullLogger.Instance);

        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await apps.FindByClientIdAsync(AuthSeed.WebClientId);
        var descriptor = new OpenIddictApplicationDescriptor();
        await apps.PopulateAsync(descriptor, client!);
        return (descriptor.RedirectUris.ToHashSet(), descriptor.PostLogoutRedirectUris.ToHashSet());
    }

    [Fact]
    public async Task Redirect_uris_follow_public_url()
    {
        try
        {
            var (login, logout) = await SeedAsync("https://stand.example/");
            Assert.Equal([new Uri("https://stand.example/authentication/login-callback")], login);
            Assert.Equal([new Uri("https://stand.example/authentication/logout-callback")], logout);

            // Адрес стенда сменился — клиент обновляется, а не остаётся со старым адресом возврата.
            (login, logout) = await SeedAsync("https://other.example");
            Assert.Equal([new Uri("https://other.example/authentication/login-callback")], login);
        }
        finally
        {
            // Клиент общий для всех тестов коллекции (они входят через https://localhost): возвращаем адрес.
            await SeedAsync("https://localhost");
        }
    }

    /// <summary>Стенд (этап 8, B8): строки первого администратора из .env убирают после входа — предупреждать при каждом запуске не о чем.</summary>
    [Fact]
    public async Task No_warning_about_first_admin_when_admin_exists()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"seed-{Guid.NewGuid():N}@example.test";
        var admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Засев Проверка" };
        Assert.True((await users.CreateAsync(admin)).Succeeded);
        Assert.True((await users.AddToRoleAsync(admin, AppRoles.Admin)).Succeeded);
        var logger = new WarningLogger();
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:PublicUrl"] = "https://localhost" }).Build();
            await AuthSeed.RunAsync(scope.ServiceProvider, config, logger);
        }
        finally
        {
            await users.DeleteAsync(admin);
        }
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Seed:AdminEmail"));
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level >= LogLevel.Warning) Warnings.Add(formatter(state, error));
        }
    }
}
