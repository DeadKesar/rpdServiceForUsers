using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core;
using RpdFos.Core.Data;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);

// Прикладное ядро: EF Core (PostgreSQL, snake_case) и перехватчик версий.
// С этапа 2 тот же вызов появится в фоновом обработчике.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default");
builder.Services.AddCore(connectionString);

// Единая обработка ошибок: ответы в формате ProblemDetails,
// конфликт версий -> 409 в одном месте, а не в каждом эндпоинте.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();

builder.Services.AddCarter();
builder.Services.AddOpenApi();

builder.Services.AddCors(o => o.AddPolicy("Dev", p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// Автоприменение миграций — только когда разрешено конфигом (в Development).
if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors("Dev");

app.MapOpenApi();                                                              // /openapi/v1.json
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "RpdFos API v1")); // /swagger
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapCarter();

app.Run();

public partial class Program; // точка входа видна WebApplicationFactory в тестах
