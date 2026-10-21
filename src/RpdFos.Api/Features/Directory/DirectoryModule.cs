using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Features.Directory;

/// <summary>
/// Срез «Справочник сотрудников»: поиск записей по началу фамилии.
/// <list type="bullet">
/// <item>Администратору — записи без учётной записи: при создании учётной записи её привязывают к существующей записи
/// (заведующий или директор из файла должностей входит «самим собой», а не новой записью справочника).</item>
/// <item>Авторам документов — все активные записи, с учётной записью и без: составитель РПД может и не входить
/// в систему.</item>
/// </list>
/// </summary>
public class DirectoryModule : ICarterModule
{
    private const int MaxResults = 20;

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/directory").WithTags("Directory").RequireAuthorization(Policies.Admin);

        admin.MapGet("/", async (string? surname, AppDbContext db, CancellationToken ct) =>
        {
            var query = BySurname(db.Employees.Where(e => e.IsActive && e.IdentitySubject == null), surname);
            return await DirectoryRecordItem.Project(query.OrderBy(e => e.ShortName).Take(MaxResults), db).ToListAsync(ct);
        });

        var authors = app.MapGroup("/api/directory").WithTags("Directory").RequireAuthorization(Policies.DocumentAuthors);

        // Составители документа: подпись для документа и должность копируются в лист согласования, кафедра — чтобы
        // различить однофамильцев.
        authors.MapGet("/", async (string? surname, AppDbContext db, CancellationToken ct) =>
        {
            var query = BySurname(db.Employees.Where(e => e.IsActive), surname);
            return await query.OrderBy(e => e.ShortName).Take(MaxResults)
                .Select(e => new DirectoryPersonItem(
                    e.Id, e.ShortName, e.NameForDocument, e.Position,
                    db.Departments.Where(d => d.Id == e.DepartmentId).Select(d => d.ShortName).FirstOrDefault()))
                .ToListAsync(ct);
        });
    }

    // Поиск по началу фамилии («Фамилия И. О.»), без учёта регистра; пустой запрос — первые по алфавиту.
    private static IQueryable<Employee> BySurname(IQueryable<Employee> query, string? surname)
    {
        if (string.IsNullOrWhiteSpace(surname)) return query;
        var prefix = surname.Trim().ToLower();
        return query.Where(e => e.ShortName.ToLower().StartsWith(prefix));
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

/// <summary>Сотрудник для выбора составителя: «Фамилия И. О.», подпись «И. О. Фамилия», должность, кафедра.</summary>
public record DirectoryPersonItem(Guid Id, string ShortName, string NameForDocument, string? Position, string? Department);
