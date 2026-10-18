using Carter;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);

// Прикладное ядро: EF Core (PostgreSQL, snake_case), перехватчик версий, таблицы Identity и OpenIddict.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default");
builder.Services.AddCore(connectionString);

// Учётные записи: ASP.NET Core Identity. Пароли хранятся только здесь, в виде хеша.
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

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
        .RequireRole(AppRoles.Admin));

builder.Services.AddRazorPages(); // страница входа /Account/Login

// Единая обработка ошибок: ответы в формате ProblemDetails, конфликт версий -> 409.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();

builder.Services.AddCarter();
builder.Services.AddOpenApi();

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
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();                                                              // /openapi/v1.json
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "RpdFos API v1")); // /swagger
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorPages();
app.MapCarter();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program; // точка входа видна WebApplicationFactory в тестах
