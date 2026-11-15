using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>
/// Оболочка клиента (<c>index.html</c>) отдаётся без кэша браузера (этап 6.1, C1): иначе после пересборки клиента
/// браузер открывает старую оболочку со ссылками на файлы, которых уже нет, и страница пуста.
/// </summary>
public sealed class ClientShellTests(ClientShellFactory factory) : IClassFixture<ClientShellFactory>
{
    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), // без перенаправления на https
        AllowAutoRedirect = false                   // перенаправление, если оно появится, не должно пройти незамеченным
    });

    [Theory]
    [InlineData("/")]           // запасной маршрут: корень
    [InlineData("/documents")]  // запасной маршрут: адрес страницы клиента
    [InlineData("/index.html")] // сам файл, обычная раздача статических файлов
    public async Task Shell_is_served_with_no_cache(string path)
    {
        var response = await CreateClient().GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.Equal(ClientShellFactory.Shell, await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.Null(response.Headers.CacheControl.MaxAge);
    }

    [Fact]
    public async Task Other_static_files_are_left_as_before()
    {
        var response = await CreateClient().GetAsync("/css/app.css");

        response.EnsureSuccessStatusCode();
        Assert.Null(response.Headers.CacheControl);
    }
}

/// <summary>
/// Хост в окружении Testing (база не нужна) с собственным веб-корнем: в этом окружении клиент <c>RpdFos.Web</c>
/// к хосту не подключается (статические файлы проекта-клиента подключаются только в Development), поэтому оболочку
/// и один обычный файл подкладывает тест.
/// </summary>
public sealed class ClientShellFactory : WebApplicationFactory<Program>
{
    public const string Shell = "<!DOCTYPE html><title>shell</title>";

    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "rpdfos-tests", Guid.NewGuid().ToString("N"));

    public ClientShellFactory()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "css"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), Shell);
        File.WriteAllText(Path.Combine(_webRoot, "css", "app.css"), "body { margin: 0 }");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // appsettings.Development.json не загружается, база не нужна
        builder.UseWebRoot(_webRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_webRoot, recursive: true); }
        catch (IOException) { } // временный каталог: не удалился сейчас — уберёт очистка временной папки
    }
}
