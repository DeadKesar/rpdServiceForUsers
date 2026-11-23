using Carter;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Accounts;
using RpdFos.Core;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.Mail;
using RpdFos.Core.Printing;
using RpdFos.Core.Reminders;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);

// Прикладное ядро: EF Core (PostgreSQL, snake_case), перехватчик версий, таблицы Identity и OpenIddict.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default");
builder.Services.AddCore(connectionString);
// Настройки правил проверки содержимого: например, предельный возраст основной литературы.
builder.Services.Configure<ValidationRuleSettings>(builder.Configuration.GetSection("ValidationRules"));
// Сформированные файлы: каталог и срок хранения; относительный каталог — от каталога приложения.
builder.Services.Configure<GenerationSettings>(builder.Configuration.GetSection("Generation"));
builder.Services.PostConfigure<GenerationSettings>(o =>
    o.StoragePath = Path.GetFullPath(o.StoragePath, builder.Environment.ContentRootPath));
// Почта: сервер, отправитель, адрес системы для ссылок. В разработке письма уходят в ловушку из docker-compose (этап 7, A4).
builder.Services.Configure<MailSettings>(builder.Configuration.GetSection("Mail"));
var mailEnabled = builder.Configuration.GetValue("Mail:Enabled", true);
// Срок доработки и напоминания о нём: 14 дней, предупреждение за 3 (этап 7, A6).
builder.Services.Configure<RemindersSettings>(builder.Configuration.GetSection("Reminders"));
// Сроки ссылок из служебных писем: сутки у восстановления пароля, трое суток у приглашения (этап 8, A6).
builder.Services.Configure<AccountSettings>(builder.Configuration.GetSection("Accounts"));

// Учётные записи: ASP.NET Core Identity. Пароли хранятся только здесь, в виде хеша.
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    // Ссылки «Забыли пароль?» и приглашения: сроки, провайдер токена приглашения, русские ошибки (этап 8, A6).
    .AddAccountLinks();

// Сервер авторизации OpenIddict внутри хоста API: OpenID Connect, authorization code + PKCE.
// Основа — официальный пример OpenIddict «Balosar».
builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
    .AddServer(o =>
    {
        o.SetAuthorizationEndpointUris("connect/authorize")
         .SetTokenEndpointUris("connect/token")
         .SetEndSessionEndpointUris("connect/logout")
         .SetUserInfoEndpointUris("connect/userinfo");

        o.RegisterScopes(
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Roles);

        o.AllowAuthorizationCodeFlow()
         .AllowRefreshTokenFlow();

        // Ключи подписи и шифрования для разработки; на сервере (этап 7) — настоящие сертификаты.
        o.AddDevelopmentEncryptionCertificate()
         .AddDevelopmentSigningCertificate();

        o.UseAspNetCore()
         .EnableAuthorizationEndpointPassthrough()
         .EnableTokenEndpointPassthrough()
         .EnableEndSessionEndpointPassthrough();
    })
    .AddValidation(o =>
    {
        // Токены проверяет этот же хост — без обращения к внешнему серверу.
        o.UseLocalServer();
        o.UseAspNetCore();
    });

// Доступ к API — только с токеном доступа; роли берутся из токена.
builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new AuthorizationPolicyBuilder(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(Policies.MethodistOrAdmin, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Methodist, AppRoles.Admin))
    .AddPolicy(Policies.Admin, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Admin))
    .AddPolicy(Policies.DocumentAuthors, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Teacher, AppRoles.Methodist, AppRoles.Admin))
    .AddPolicy(Policies.Reviewers, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Methodist))
    .AddPolicy(Policies.Approvers, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Approver))
    .AddPolicy(Policies.Commenters, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Methodist, AppRoles.Approver))
    .AddPolicy(Policies.CommentResolvers, p => p
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireRole(AppRoles.Teacher, AppRoles.Methodist, AppRoles.Admin, AppRoles.Approver))
    .AddPolicy(Policies.ImageReaders, p => p
        // Первой — схема токена: без входа ответ 401, а не переход на страницу входа.
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, IdentityConstants.ApplicationScheme)
        .RequireAuthenticatedUser())
    .AddPolicy(Policies.JobsDashboard, p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
        .RequireRole(AppRoles.Admin));

builder.Services.AddRazorPages(); // страница входа /Account/Login

// Единая обработка ошибок: ответы в формате ProblemDetails с русскими заголовками, конфликт версий -> 409.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemTitles.Apply);
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();

builder.Services.AddCarter();
builder.Services.AddOpenApi();

// Фоновые задачи — Hangfire: хранилище в той же PostgreSQL, отдельная схема hangfire.
// Jobs:Enabled = false (окружение Testing) — без сервера задач и панели: база при старте не нужна.
var jobs = builder.Configuration.GetSection("Jobs");
var jobsEnabled = jobs.GetValue("Enabled", true);
builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    // Импорт сам пишет итог в parser_runs (в том числе failed): автоматический повтор не нужен.
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 }));
// Хранилище — обычный синглтон DI, а не UsePostgreSqlStorage: тот пишет его в статический JobStorage.Current,
// и два хоста в одном процессе (тесты) перезаписывали бы хранилище друг друга. Создаётся при первом
// обращении (сервер задач, панель, постановка задачи) — без них база при старте не нужна.
// Сначала — IGlobalConfiguration: только при его создании применяются настройки выше (уровень совместимости 1.8
// нужен, чтобы сохранялась очередь задачи; без него задача уходит в очередь default, которую никто не слушает).
builder.Services.AddSingleton<JobStorage>(sp =>
{
    sp.GetRequiredService<IGlobalConfiguration>();
    var options = new PostgreSqlStorageOptions
    {
        SchemaName = "hangfire",
        QueuePollInterval = TimeSpan.FromSeconds(jobs.GetValue("QueuePollSeconds", 15))
    };
    return new PostgreSqlStorage(new NpgsqlConnectionFactory(connectionString, options), options);
});
// По той же причине — свой активатор: по умолчанию сервер берёт статический JobActivator.Current
// и создавал бы задачи из контейнера зависимостей чужого хоста.
builder.Services.AddSingleton<JobActivator>(sp =>
{
    sp.GetRequiredService<IGlobalConfiguration>();
    return new Hangfire.AspNetCore.AspNetCoreJobActivator(sp.GetRequiredService<IServiceScopeFactory>());
});
if (jobsEnabled)
{
    builder.Services.AddHangfireServer(o =>
    {
        // Отдельная очередь разбора с одним исполнителем: импорты выполняются по одному.
        o.ServerName = $"{Environment.MachineName}:{JobQueues.Parsing}";
        o.Queues = [JobQueues.Parsing];
        o.WorkerCount = 1;
    });
    builder.Services.AddHangfireServer(o =>
    {
        // Формирование документов — параллельно, но не больше, чем выдержит конвертер.
        o.ServerName = $"{Environment.MachineName}:{JobQueues.Generation}";
        o.Queues = [JobQueues.Generation];
        o.WorkerCount = jobs.GetValue("GenerationWorkers", 2);
    });
    if (mailEnabled)
        builder.Services.AddHangfireServer(o =>
        {
            // Письма — одним исполнителем: порядок отправки сохраняется, а повторный запуск не перехватывает письма идущего.
            o.ServerName = $"{Environment.MachineName}:{JobQueues.Mail}";
            o.Queues = [JobQueues.Mail];
            o.WorkerCount = 1;
        });
}

// Конвертер DOCX -> PDF — отдельный сервис (docker-compose: converter); LibreOffice на большом документе небыстр.
builder.Services.AddHttpClient<IPdfConverter, GotenbergPdfConverter>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration.GetValue("Converter:Url", "http://localhost:3000")!.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Converter:TimeoutSeconds", 120));
});

var app = builder.Build();

// Миграции и начальные данные (роли, клиент OIDC, первый администратор) — в Development.
if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await AuthSeed.RunAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.UseWebAssemblyDebugging();

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles(); // веб-клиент Blazor раздаётся этим же хостом: один адрес, CORS не нужен
app.UseStaticFiles(ClientShell.CreateStaticFileOptions()); // index.html — без кэша браузера, остальное как обычно

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();                                                              // /openapi/v1.json
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "RpdFos API v1")); // /swagger
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorPages();
app.MapCarter();
if (jobsEnabled)
{
    // Проверку доступа делает политика конечной точки; фильтры самой панели отключены.
    app.MapHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [], DashboardTitle = "Фоновые задачи" })
       .RequireAuthorization(Policies.JobsDashboard);
    // Расписание — в JobSchedule: час и пояс напоминаний проверяются тестом. Настройки уже проверены при старте.
    JobSchedule.Register(app.Services.GetRequiredService<IRecurringJobManager>(),
        app.Services.GetRequiredService<IOptions<MailSettings>>().Value,
        app.Services.GetRequiredService<IOptions<RemindersSettings>>().Value,
        app.Services.GetRequiredService<TimeProvider>().LocalTimeZone);
}
app.MapFallbackToFile(ClientShell.FileName, ClientShell.CreateStaticFileOptions()); // адреса клиента: /, /documents…

await app.RunAsync();

public partial class Program; // точка входа видна WebApplicationFactory в тестах
