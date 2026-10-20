using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Interceptors;
using RpdFos.Core.Directory;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Import;
using RpdFos.Core.Templates;

namespace RpdFos.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует прикладное ядро: EF Core (PostgreSQL, snake_case), сквозные механизмы
    /// и таблицы OpenIddict. Вызывается из API, а с этапа 2 — и из фонового обработчика.
    /// </summary>
    public static IServiceCollection AddCore(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<VersioningInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .UseOpenIddict()
            .AddInterceptors(sp.GetRequiredService<VersioningInterceptor>()));
        services.AddScoped<ReferenceImporter>();
        services.AddScoped<DirectoryAccounts>();
        services.AddScoped<ImportJob>();
        services.AddSingleton<DocumentTypeRegistry>();
        services.AddScoped<TemplateLifecycle>();
        return services;
    }
}
