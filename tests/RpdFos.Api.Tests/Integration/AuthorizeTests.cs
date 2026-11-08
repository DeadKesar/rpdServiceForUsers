using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using RpdFos.Api.Infrastructure;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Конечная точка авторизации OpenIddict (этап 6, A4): тихий вход без задержки.</summary>
[Collection(DatabaseCollection.Name)]
public class AuthorizeTests(DatabaseFixture app)
{
    private const string Callback = "https://localhost/authentication/login-callback";

    [Fact]
    public async Task Silent_sign_in_without_session_returns_login_required_at_once()
    {
        await EnsureClientAsync();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/connect/authorize?client_id=" + AuthSeed.WebClientId +
            "&redirect_uri=" + Uri.EscapeDataString(Callback) + "&response_type=code&scope=openid%20profile%20roles" +
            "&state=s1&code_challenge=WHwshNnskf5ouAYFAaPSo8sesP9VQemypR0vPFRYdPA&code_challenge_method=S256" +
            "&response_mode=query&prompt=none");

        // Не страница входа, а сразу адрес возврата клиента с ошибкой.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(Callback, location);
        Assert.Contains("error=login_required", location);
        Assert.Contains("state=s1", location);
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
                Permissions.Endpoints.Authorization, Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code, Permissions.Scopes.Profile, Permissions.Scopes.Roles
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });
    }
}
