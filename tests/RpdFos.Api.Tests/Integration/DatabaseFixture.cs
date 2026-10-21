using System.Net.Http.Headers;
using DisciplineDataExtractor;
using RpdFos.Core.Import;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
public class DatabaseFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Сервер фоновых задач. У Hangfire много статического состояния (конфигурация, фильтры), поэтому
    /// в процессе тестов сервер задач — только в одном хосте; хостам без задач он не нужен.
    /// </summary>
    protected virtual bool JobsEnabled => true;

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Каталог сформированных файлов этого хоста.</summary>
    public string GeneratedFiles { get; } = Path.Combine(Path.GetTempPath(), "rpdfos-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Testing: appsettings.Development.json не загружается, автомиграций и засева нет.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
        // Фоновые задачи — на той же базе в контейнере; очередь опрашивается раз в секунду, чтобы тесты не ждали.
        builder.UseSetting("Jobs:Enabled", JobsEnabled ? "true" : "false");
        builder.UseSetting("Jobs:QueuePollSeconds", "1");
        // Сформированные файлы — во временный каталог: тесты не пишут в каталог приложения.
        builder.UseSetting("Generation:StoragePath", GeneratedFiles);

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);

            // Политики требуют схему OpenIddict (API) и cookie входа (панель задач); они остаются,
            // но передают проверку тестовой схеме.
            services.PostConfigure<OpenIddictValidationAspNetCoreOptions>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
                o => o.ForwardDefault = TestAuthHandler.SchemeName);
            services.PostConfigure<CookieAuthenticationOptions>(
                IdentityConstants.ApplicationScheme,
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

    private Task<Guid>? _realPlan;

    /// <summary>
    /// Учебный план 09.04.04 из реальных файлов (tests/fixtures), импортированный один раз на базу: дисциплины,
    /// часы по семестрам, матрица компетенций. Тесты одной коллекции идут по очереди, поэтому без блокировок.
    /// </summary>
    public Task<Guid> RealPlanIdAsync() => _realPlan ??= ImportRealPlanAsync();

    private async Task<Guid> ImportRealPlanAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var methodist = await ImportFiles.AddMethodistAsync(db);
        var (run, uploaded) = await ImportFiles.AddRunAsync(db, methodist.Id, ImportFiles.Read());
        SourceFile Source(int i) => new(uploaded[i].OriginalName, uploaded[i].Content!);
        var result = new DataExtractor().Extract(new ExtractionInput(Source(0), Source(1), Source(2), Source(3)));
        run = await scope.ServiceProvider.GetRequiredService<ReferenceImporter>().ImportAsync(run.Id, result);
        return run.CreatedCurriculumPlanId ?? throw new InvalidOperationException("Учебный план не импортирован.");
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
        if (System.IO.Directory.Exists(GeneratedFiles)) System.IO.Directory.Delete(GeneratedFiles, recursive: true);
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}

/// <summary>Импорт — в отдельной базе: его проверки считают строки справочников.</summary>
[CollectionDefinition(Name)]
public sealed class ImportCollection : ICollectionFixture<ImportDatabaseFixture>
{
    public const string Name = "import";
}

/// <summary>База для тестов конвейера импорта: конвейер вызывается напрямую, сервер задач не нужен.</summary>
public sealed class ImportDatabaseFixture : DatabaseFixture
{
    protected override bool JobsEnabled => false;
}
