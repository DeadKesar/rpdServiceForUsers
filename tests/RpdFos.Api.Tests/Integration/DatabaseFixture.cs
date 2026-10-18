using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using Testcontainers.PostgreSql;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Хост API на чистой PostgreSQL 17 в контейнере (Testcontainers): миграции применяются при старте,
/// аутентификация — тестовая схема. Один контейнер на все тесты коллекции <see cref="DatabaseCollection"/>.
/// </summary>
public sealed class DatabaseFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Testing: appsettings.Development.json не загружается, автомиграций и засева нет.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);

            // Политики требуют схему OpenIddict; она остаётся, но передаёт проверку тестовой схеме.
            services.PostConfigure<OpenIddictValidationAspNetCoreOptions>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
                o => o.ForwardDefault = TestAuthHandler.SchemeName);
        });
    }

    /// <summary>
    /// Клиент API от имени пользователя с ролью; без роли — анонимный. <paramref name="subject"/> — утверждение sub
    /// (id учётной записи), чтобы запрос связался с записью справочника сотрудников.
    /// </summary>
    public HttpClient CreateClientFor(string? role, string? subject = null)
    {
        // OpenIddict принимает только HTTPS, поэтому и тестовый клиент ходит по https.
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (role is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        if (subject is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject);
        return client;
    }

    /// <summary>Прямой доступ к базе — подготовить данные в обход API.</summary>
    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        await WithDbAsync(async db =>
        {
            // Защита от ошибки конфигурации: тесты пишут только в контейнер, не в базу разработки.
            if (db.Database.GetConnectionString() != _db.GetConnectionString())
                throw new InvalidOperationException("Тестовый хост подключён не к базе в контейнере.");

            await db.Database.MigrateAsync();
        });

        // Роли — как засев в Development (в окружении Testing засев не запускается).
        await using var scope = Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in AppRoles.All)
            await roles.CreateAsync(new IdentityRole<Guid>(role));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _db.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}

/// <summary>Импорт — в отдельной базе: его проверки считают строки справочников.</summary>
[CollectionDefinition(Name)]
public sealed class ImportCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "import";
}
