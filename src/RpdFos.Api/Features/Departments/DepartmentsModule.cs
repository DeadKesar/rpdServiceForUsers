using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Directory;

namespace RpdFos.Api.Features.Departments;

/// <summary>
/// Срез «Кафедры» (этап 8, B2; FR-ADM-06, S-51): список и правка названия и краткого названия — администратору. Правила и
/// запись аудита — в ядре (<see cref="DepartmentDirectory"/>).
/// </summary>
public class DepartmentsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/departments").WithTags("Departments").RequireAuthorization(Policies.Admin);

        admin.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            await db.Departments.AsNoTracking().OrderBy(d => d.Name)
                .Select(d => new DepartmentItem(d.Id, d.Code, d.Name, d.ShortName, d.Institute == null ? null : d.Institute.ShortName ?? d.Institute.Code,
                    d.IsEditedManually))
                .ToListAsync(ct));

        admin.MapPut("/{id:guid}", async (Guid id, DepartmentRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts accounts,
            DepartmentDirectory directory, CancellationToken ct) =>
        {
            if (await accounts.FindIdAsync(http.User, ct) is not Guid by)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
                    detail: "Учётная запись не связана со справочником сотрудников.");
            var (department, problem) = await directory.UpdateAsync(id, rq.Name, rq.ShortName, by, ct);
            if (problem is not null)
                return Results.Problem(statusCode: problem.Kind switch
                {
                    DirectoryProblemKind.NotFound => StatusCodes.Status404NotFound,
                    DirectoryProblemKind.Conflict => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status422UnprocessableEntity
                }, title: problem.Title, detail: problem.Detail);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await db.Departments.AsNoTracking().Where(d => d.Id == department!.Id)
                .Select(d => new DepartmentItem(d.Id, d.Code, d.Name, d.ShortName, d.Institute == null ? null : d.Institute.ShortName ?? d.Institute.Code,
                    d.IsEditedManually))
                .SingleAsync(ct));
        });
    }
}

/// <summary>Кафедра на странице: номер, название, краткое название, институт (краткое название или код), правка вручную.</summary>
public record DepartmentItem(Guid Id, string Code, string Name, string ShortName, string? Institute, bool IsEditedManually);

public record DepartmentRequest(string? Name, string? ShortName);
