using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Подписант места листа согласования: ФИО для документа («И. О. Фамилия»), должность, сокращение кафедры.
/// Не найден — поля пустые: место печатается пустой строкой, а в отчёте о формировании — предупреждение.
/// </summary>
public sealed record ResolvedSignatory(string SeatId, string Title, Guid? EmployeeId, string? Name, string? Position, string? Department)
{
    public bool Found => EmployeeId is not null;
}

/// <summary>Подписанты документа по местам листа согласования шаблона и предупреждения о ненайденных.</summary>
public sealed record SignatoryReport(IReadOnlyList<ResolvedSignatory> Seats, IReadOnlyList<string> Warnings)
{
    public ResolvedSignatory? Seat(string id) => Seats.FirstOrDefault(s => s.SeatId == id);
}

/// <summary>Что известно резолверу: база и кафедра дисциплины документа.</summary>
public sealed record SignatoryContext(AppDbContext Db, Department? Department);

/// <summary>
/// Звено цепочки ответственности (FR-REV-12): правило определения подписанта из <c>signatures_config</c>
/// («lookup.role:umu_head», «discipline.department.head») обрабатывает первое звено, которое его понимает.
/// Новое правило — новое звено, без правки остальных.
/// </summary>
public abstract class SignatoryResolver
{
    public SignatoryResolver? Next { get; init; }

    public Task<Employee?> ResolveAsync(string rule, SignatoryContext context, CancellationToken ct) =>
        Handles(rule) ? FindAsync(rule, context, ct)
        : Next is not null ? Next.ResolveAsync(rule, context, ct)
        : Task.FromResult<Employee?>(null);

    protected abstract bool Handles(string rule);
    protected abstract Task<Employee?> FindAsync(string rule, SignatoryContext context, CancellationToken ct);

    // Подписант — только активная запись справочника; при совпадении — по алфавиту, чтобы выбор был устойчивым.
    protected static Task<Employee?> FirstAsync(IQueryable<Employee> query, CancellationToken ct) =>
        query.Where(e => e.IsActive).OrderBy(e => e.ShortName).FirstOrDefaultAsync(ct);
}

/// <summary><c>lookup.role:&lt;категория&gt;</c> — активная запись справочника с этой категорией подписанта.</summary>
public sealed class LookupRoleResolver : SignatoryResolver
{
    public const string Prefix = "lookup.role:";

    protected override bool Handles(string rule) => rule.StartsWith(Prefix, StringComparison.Ordinal);

    protected override Task<Employee?> FindAsync(string rule, SignatoryContext context, CancellationToken ct)
    {
        var role = rule[Prefix.Length..];
        return FirstAsync(context.Db.Employees.Where(e => e.SignatoryRole == role), ct);
    }
}

/// <summary>
/// <c>discipline.department.head</c> — заведующий кафедрой дисциплины; если заведующего нет —
/// исполняющий обязанности заведующего.
/// </summary>
public sealed class DepartmentHeadResolver : SignatoryResolver
{
    public const string Rule = "discipline.department.head";

    protected override bool Handles(string rule) => rule == Rule;

    protected override async Task<Employee?> FindAsync(string rule, SignatoryContext context, CancellationToken ct)
    {
        if (context.Department is not { } department) return null;
        var heads = context.Db.Employees.Where(e => e.DepartmentId == department.Id);
        return await FirstAsync(heads.Where(e => e.SignatoryRole == SignatoryRoles.DepartmentHead), ct)
               ?? await FirstAsync(heads.Where(e => e.SignatoryRole == SignatoryRoles.DepartmentHeadActing), ct);
    }
}

/// <summary><c>discipline.department.institute.director</c> — директор института, в который входит кафедра дисциплины.</summary>
public sealed class InstituteDirectorResolver : SignatoryResolver
{
    public const string Rule = "discipline.department.institute.director";

    protected override bool Handles(string rule) => rule == Rule;

    protected override Task<Employee?> FindAsync(string rule, SignatoryContext context, CancellationToken ct) =>
        context.Department?.InstituteId is not Guid instituteId
            ? Task.FromResult<Employee?>(null)
            : FirstAsync(context.Db.Employees.Where(e => e.SignatoryRole == SignatoryRoles.InstituteDirector && e.InstituteId == instituteId), ct);
}

/// <summary>
/// Подписанты документа по листу согласования шаблона. Председатель УМК по направлению пока определяется
/// по категории (<c>lookup.role:umk_chair</c>); связь с направлением — этап 6, как и ручное назначение (FR-REV-13).
/// </summary>
public sealed class SignatoryResolution(AppDbContext db)
{
    private static readonly SignatoryResolver Chain = new LookupRoleResolver
    {
        Next = new DepartmentHeadResolver { Next = new InstituteDirectorResolver() }
    };

    public async Task<SignatoryReport> ResolveAsync(IReadOnlyList<SignatureSeat> seats, Guid disciplineId, CancellationToken ct = default)
    {
        var departmentId = await db.Disciplines.Where(d => d.Id == disciplineId).Select(d => d.DepartmentId).SingleAsync(ct);
        var department = departmentId is Guid id ? await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) : null;
        var context = new SignatoryContext(db, department);

        var resolved = new List<ResolvedSignatory>();
        var warnings = new List<string>();
        foreach (var seat in seats)
        {
            var employee = await Chain.ResolveAsync(seat.Resolver, context, ct);
            if (employee is null)
            {
                resolved.Add(new ResolvedSignatory(seat.Id, seat.Title, null, null, null, null));
                warnings.Add($"Не найден подписант «{seat.Title}» (правило {seat.Resolver}) — место напечатано пустой строкой.");
                continue;
            }
            var departmentName = employee.DepartmentId is Guid d
                ? await db.Departments.Where(x => x.Id == d).Select(x => x.ShortName).SingleOrDefaultAsync(ct)
                : null;
            resolved.Add(new ResolvedSignatory(seat.Id, seat.Title, employee.Id, employee.NameForDocument, employee.Position, departmentName));
        }
        return new SignatoryReport(resolved, warnings);
    }
}
