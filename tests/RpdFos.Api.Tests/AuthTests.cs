using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>Аутентификация: API закрыт без токена, сервер авторизации публикует свои настройки.</summary>
public class AuthTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(string baseAddress) =>
        factory.WithWebHostBuilder(b => b.UseEnvironment("Testing"))
               .CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress) });

    [Fact]
    public async Task Api_requires_access_token()
    {
        var response = await CreateClient("http://localhost").GetAsync("/api/templates");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_document_is_published()
    {
        // OpenIddict принимает только HTTPS, поэтому тестовый клиент обращается по https.
        var response = await CreateClient("https://localhost").GetAsync("/.well-known/openid-configuration");

        response.EnsureSuccessStatusCode();
    }
}
