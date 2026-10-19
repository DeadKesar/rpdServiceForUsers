using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Features.Directory;

/// <summary>
/// Срез «Справочник сотрудников» (для Администратора): поиск записей без учётной записи — чтобы при создании
/// учётной записи привязать её к существующей записи (заведующий или директор из файла должностей входит
/// «самим собой», а не новой записью справочника).
/// </summary>
public class DirectoryModule : ICarterModule
{
    private const int MaxResults = 20;

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/directory").WithTags("Directory").RequireAuthorization(Policies.Admin);

        // Поиск по началу фамилии («Фамилия И. О.»), без учёта регистра; пустой запрос — первые по алфавиту.
        g.MapGet("/", async (string? surname, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Employees.Where(e => e.IsActive && e.IdentitySubject == null);
            if (!string.IsNullOrWhiteSpace(surname))
            {
                var prefix = surname.Trim().ToLower();
                query = query.Where(e => e.ShortName.ToLower().StartsWith(prefix));
            }
            return await DirectoryRecordItem.Project(query.OrderBy(e => e.ShortName).Take(MaxResults), db).ToListAsync(ct);
        });
    }
}

/// <summary>Запись справочника сотрудников: ФИО, должность и место, кафедра (сокращение).</summary>
public record DirectoryRecordItem(
    Guid Id, string ShortName, string? FullName, string? Position, string? OfficialDesignation, string? Department, bool IsActive)
{
    public static IQueryable<DirectoryRecordItem> Project(IQueryable<Employee> query, AppDbContext db) =>
        query.Select(e => new DirectoryRecordItem(
            e.Id, e.ShortName, e.FullName, e.Position, e.OfficialDesignation,
            db.Departments.Where(d => d.Id == e.DepartmentId).Select(d => d.ShortName).FirstOrDefault(),
            e.IsActive));
}
