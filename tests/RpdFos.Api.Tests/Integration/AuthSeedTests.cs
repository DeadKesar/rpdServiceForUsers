using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using RpdFos.Api.Infrastructure;
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

}
