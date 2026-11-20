using System.Security.Claims;
using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.Assignments;

/// <summary>
/// Срез «Закрепление дисциплин» (этап 7, A10; решение 5): методист и администратор закрепляют сотрудников за дисциплинами
/// версии плана. Страница показывает дисциплины плана, закреплённых за ними и документы по каждой; закрепляют сразу за
/// несколькими отмеченными дисциплинами, снимают по одному. Правила — в ядре (<see cref="DisciplineAssignments"/>); как
/// закрепление действует на доступ и создание документов — в срезе «Документы».
/// </summary>
public class AssignmentsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/discipline-assignments").WithTags("Assignments").RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapGet("/", Page);
        g.MapPost("/", Assign);
        g.MapDelete("/{id:guid}", Unassign);
    }

    /// <summary>
    /// Дисциплины плана, для которых создаётся документ (FR-DOC-18): индекс, название, кафедра, закреплённые и документы с
    /// автором и статусом. Удалённых документов нет в выборке (мягкое удаление), архивные показаны со своим статусом.
    /// </summary>
    private static async Task<IResult> Page(Guid planId, AppDbContext db, CancellationToken ct)
    {
        if (!await db.CurriculumPlans.AnyAsync(p => p.Id == planId, ct))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Учебный план не найден",
                detail: "Обновите страницу и выберите план из списка.");

        var disciplines = (await db.Disciplines.AsNoTracking().Where(d => d.CurriculumPlanId == planId).OrderBy(d => d.Code)
                .ToListAsync(ct))
            .Where(DocumentRules.CanHaveDocument).ToList();
        var ids = disciplines.Select(d => d.Id).ToList();

        var assigned = (await (
                    from a in db.DisciplineAssignments.AsNoTracking()
                    where ids.Contains(a.DisciplineId)
                    join e in db.Employees on a.EmployeeId equals e.Id
                    join assigner in db.Employees on a.AssignedBy equals assigner.Id
                    orderby e.ShortName
                    select new { a.DisciplineId, Item = new AssigneeItem(a.Id, e.Id, e.ShortName, a.AssignedAt, assigner.ShortName) })
                .ToListAsync(ct))
            .GroupBy(x => x.DisciplineId).ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToList());
        var documents = (await (
                    from d in db.Documents.AsNoTracking()
                    where ids.Contains(d.DisciplineId)
                    join e in db.Employees on d.AuthorId equals e.Id
                    orderby d.CreatedAt
                    select new { d.DisciplineId, d.Id, d.Type, d.Status, d.IsOutdated, Author = e.ShortName })
                .ToListAsync(ct))
            .GroupBy(x => x.DisciplineId).ToDictionary(g => g.Key,
                g => g.Select(x => new DisciplineDocument(x.Id, x.Type, x.Status, DocumentStatuses.Title(x.Status), x.IsOutdated, x.Author)).ToList());

        var departmentIds = disciplines.Select(d => d.DepartmentId).OfType<Guid>().Distinct().ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => departmentIds.Contains(d.Id))
            .OrderBy(d => d.ShortName).ToListAsync(ct);
        var shortNames = departments.ToDictionary(d => d.Id, d => d.ShortName);

        return Results.Ok(new AssignmentPage(
            departments.Select(d => new DepartmentOption(d.Id, d.Name, d.ShortName)).ToList(),
            disciplines.Select(d => new DisciplineRow(d.Id, d.Code, d.Name, d.DepartmentId,
                d.DepartmentId is Guid department ? shortNames.GetValueOrDefault(department) : null,
                assigned.GetValueOrDefault(d.Id) ?? [], documents.GetValueOrDefault(d.Id) ?? [])).ToList()));
    }

    /// <summary>
    /// Закрепить сотрудника за отмеченными дисциплинами. Уже закреплённые не повторяются и ошибкой не считаются — их число в
    /// ответе. Данные запроса (нет дисциплин, неверная дисциплина или сотрудник) — 422.
    /// </summary>
    private static async Task<IResult> Assign(
        AssignRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DisciplineAssignments assignments,
        CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();

        var result = await assignments.AssignAsync(rq.DisciplineIds ?? [], rq.EmployeeId, me, ActorRole(http.User), ct);
        if (result.Problem is { } problem) return Refused(problem, result.Detail);

        await db.SaveChangesAsync(ct);
        return Results.Ok(new AssignmentSummary(result.Added, result.AlreadyAssigned));
    }

    private static async Task<IResult> Unassign(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DisciplineAssignments assignments, CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();
        if (!await assignments.UnassignAsync(id, me, ActorRole(http.User), ct))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Закрепление не найдено",
                detail: "Возможно, его уже сняли. Обновите страницу.");

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult Refused(AssignmentProblem problem, string? detail) => problem switch
    {
        AssignmentProblem.NoDisciplines => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Не выбраны дисциплины", detail: "Отметьте в таблице дисциплины, за которыми закрепляется сотрудник."),
        AssignmentProblem.DisciplineNotFound => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Дисциплина не найдена", detail: "Одной из выбранных дисциплин нет в базе. Обновите страницу и повторите."),
        AssignmentProblem.NotADiscipline => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Для этой строки плана документ не создаётся", detail: detail),
        AssignmentProblem.EmployeeNotFound => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Сотрудник не найден", detail: "Выберите сотрудника в справочнике: запись должна быть действующей."),
        _ => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "У сотрудника нет учётной записи преподавателя",
            detail: "Закреплённый входит в систему и создаёт документы как преподаватель: создайте или включите ему учётную запись " +
                    "с ролью «Преподаватель» на странице «Пользователи».")
    };

    // Роль, от которой действует пользователь, — для текста уведомлений: методист назван раньше администратора.
    private static string ActorRole(ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.Methodist) ? AppRoles.Methodist : AppRoles.Admin;

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");
}

/// <summary>Закрепить сотрудника (запись справочника) за дисциплинами версии плана.</summary>
public record AssignRequest(Guid EmployeeId, List<Guid>? DisciplineIds);

/// <summary>Итог «Закрепить»: сколько закреплений добавлено и сколько дисциплин уже были за сотрудником.</summary>
public record AssignmentSummary(int Added, int AlreadyAssigned);

public record DepartmentOption(Guid Id, string Name, string ShortName);

/// <summary>Страница «Закрепление дисциплин» для плана: кафедры его дисциплин (для фильтра) и сами дисциплины.</summary>
public record AssignmentPage(List<DepartmentOption> Departments, List<DisciplineRow> Disciplines);

public record DisciplineRow(
    Guid Id, string Code, string Name, Guid? DepartmentId, string? Department, List<AssigneeItem> Assigned,
    List<DisciplineDocument> Documents);

/// <param name="Id">Идентификатор закрепления — по нему оно снимается.</param>
public record AssigneeItem(Guid Id, Guid EmployeeId, string Name, DateTimeOffset AssignedAt, string AssignedBy);

public record DisciplineDocument(Guid Id, string Type, string Status, string StatusTitle, bool IsOutdated, string AuthorName);
