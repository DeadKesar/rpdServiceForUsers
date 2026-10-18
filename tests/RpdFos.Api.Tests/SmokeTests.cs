using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>Дымовой тест ходячего скелета: приложение поднимается и отвечает.</summary>
public class SmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        // Окружение Testing: appsettings.Development.json не загружается,
        // автомиграции выключены, база данных для теста не нужна.
        var client = factory
            .WithWebHostBuilder(b => b.UseEnvironment("Testing"))
            .CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }
}
