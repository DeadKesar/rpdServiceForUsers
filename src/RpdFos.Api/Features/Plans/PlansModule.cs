using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;

namespace RpdFos.Api.Features.Plans;

/// <summary>
/// Срез «Учебные планы»: импортированные версии планов — для выбора плана шаблона. Только чтение:
/// планы создаёт импорт.
/// </summary>
public class PlansModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/curriculum-plans").WithTags("Plans").RequireAuthorization();

        g.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            await PlanItem.Query(db)
                .OrderBy(p => p.DirectionCode).ThenBy(p => p.ProfileName).ThenByDescending(p => p.Year)
                .ThenByDescending(p => p.Version)
                .ToListAsync(ct));
    }
}

/// <summary>Версия учебного плана: направление, профиль, год набора, форма обучения, номер версии.</summary>
public record PlanItem
{
    public Guid Id { get; init; }
    public string DirectionCode { get; init; } = "";
    public string DirectionName { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public int Year { get; init; }
    public string StudyForm { get; init; } = "";
    public int Version { get; init; }

    // Инициализация свойств, а не конструктор: так EF переводит в SQL и фильтры, и сортировку по полям проекции.
    public static IQueryable<PlanItem> Query(AppDbContext db) =>
        from p in db.CurriculumPlans
        join d in db.StudyDirections on p.DirectionId equals d.Id
        join pr in db.StudyProfiles on p.ProfileId equals pr.Id
        select new PlanItem
        {
            Id = p.Id, DirectionCode = d.Code, DirectionName = d.Name, ProfileName = pr.Name, Year = p.Year,
            StudyForm = p.StudyForm, Version = p.Version
        };
}
