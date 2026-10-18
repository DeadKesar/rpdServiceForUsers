using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Interceptors;

namespace RpdFos.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует прикладное ядро: EF Core (PostgreSQL, snake_case) и сквозные механизмы.
    /// Вызывается из API, а с этапа 2 — и из фонового обработчика.
    /// </summary>
    public static IServiceCollection AddCore(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<VersioningInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<VersioningInterceptor>()));
        return services;
    }
}
