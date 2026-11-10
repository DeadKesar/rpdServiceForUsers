using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Api.Features.Plans;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.Approvals;

/// <summary>
/// Срез «Согласование» (этап 6, B2–B3; UC-07 – UC-09): очередь согласующего (S-31), отметки «Согласовать» и
/// «Вернуть на проверку», ручное назначение подписанта (FR-REV-13) и запуск согласования принятого документа.
/// Правила — в ядре (<see cref="DocumentApproval"/>, <see cref="DocumentTransitions"/>). Состояние согласования
/// документа приходит вместе с ним (<see cref="DocumentDetails.Approval"/>).
/// </summary>
public class ApprovalsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/approvals", Queue).WithTags("Approvals").RequireAuthorization(Policies.Approvers);

        var g = app.MapGroup("/api/documents/{id:guid}/approval").WithTags("Approvals").RequireAuthorization();
        g.MapPost("/start", Start).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/seats/{role}/assign", Assign).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapPost("/approve", Approve).RequireAuthorization(Policies.Approvers);
        g.MapPost("/reject", Reject).RequireAuthorization(Policies.Approvers);
    }

    /// <summary>
    /// Документы, ждущие отметки текущего пользователя (FR-REV-05): порядок согласования не задан, в списке —
    /// от отправленных раньше к отправленным позже.
    /// </summary>
    private static async Task<IResult> Queue(HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();

        var waiting = db.DocumentSignatures.Where(s => s.ResolvedUserId == me && s.Status == SignatureStatuses.Pending)
            .Select(s => s.DocumentId);
        var rows = await (
                from d in db.Documents.AsNoTracking()
                where d.Status == DocumentStatuses.Approval && waiting.Contains(d.Id)
                join x in db.Disciplines on d.DisciplineId equals x.Id
                join a in db.Employees on d.AuthorId equals a.Id
                select new
                {
                    d.Id, d.Type, x.Code, x.Name, x.CurriculumPlanId, Author = a.ShortName,
                    // Дата отправки на согласование — последний переход в этот статус.
                    SentAt = db.DocumentStatusHistory.Where(h => h.DocumentId == d.Id && h.ToStatus == DocumentStatuses.Approval)
                        .Max(h => (DateTimeOffset?)h.ChangedAt)
                })
            .ToListAsync(ct);
        var planIds = rows.Select(r => r.CurriculumPlanId).Distinct().ToList();
        var plans = await PlanItem.Query(db).Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        return Results.Ok(rows.OrderBy(r => r.SentAt).Select(r => new ApprovalQueueItem(
            r.Id, r.Type, r.Code, r.Name, r.Author, plans.GetValueOrDefault(r.CurriculumPlanId), r.SentAt)).ToList());
    }

    /// <summary>
    /// «Запустить согласование» принятого документа — после ручного назначения подписантов. Как и приём, это
    /// решение методиста о содержимом, поэтому сверяется версия клиента.
    /// </summary>
    private static async Task<IResult> Start(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentApproval approval, CancellationToken ct)
    {
        var (document, me, refusal) = await DocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;

        db.Entry(document!).Property(d => d.Version).OriginalValue = rq.Version;
        var (problem, start) = await approval.StartAsync(document!, me, ct);
        if (problem == ApprovalProblem.Unassigned)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Не все согласующие назначены",
                detail: $"Назначьте подписантов: {string.Join("; ", start!.Unassigned)}.");
        if (problem is not null) return Refused(document!, DocumentTransitions.StartApprovalAction);

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ApprovalChanged(document!.Version, document.Status, DocumentStatuses.Title(document.Status), start!.Unassigned));
    }

    /// <summary>Назначить или заменить подписанта места (FR-REV-13): методист и администратор, пока строка ждёт отметки.</summary>
    private static async Task<IResult> Assign(
        Guid id, string role, AssignRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentApproval approval, CancellationToken ct)
    {
        var (document, me, refusal) = await DocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;

        var problem = await approval.AssignAsync(document!, role, rq.EmployeeId, me, ct);
        if (problem is not null)
            return problem switch
            {
                ApprovalProblem.UnknownSeat => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                    title: "Место согласования не найдено", detail: $"Места «{role}» в согласовании документа нет."),
                ApprovalProblem.AlreadyMarked => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Отметка уже поставлена", detail: "Подписанта заменяют, пока его отметки ждут."),
                ApprovalProblem.UnknownEmployee => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Сотрудник не найден", detail: "Выберите активную запись справочника сотрудников."),
                _ => Refused(document!, DocumentTransitions.AssignAction)
            };

        await db.SaveChangesAsync(ct);
        return Results.Ok(new ApprovalChanged(document!.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    /// <summary>
    /// «Согласовать»: отметка в своей строке; отметили все — документ «Согласован». Версию клиента отметка не
    /// сверяет: содержимое на согласовании не меняется, а отметки других согласующих мешать не должны — согласование
    /// параллельное. Одновременные отметки упорядочивает версия документа, прочитанная в этом же запросе.
    /// </summary>
    private static async Task<IResult> Approve(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentApproval approval, CancellationToken ct)
    {
        var (document, me, refusal) = await DocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;

        if (await approval.ApproveAsync(document!, me, ct) is { } problem) return SignRefused(document!, problem, DocumentTransitions.ApproveAction);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ApprovalChanged(document!.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    /// <summary>«Вернуть на проверку» с обязательным комментарием: документ уходит методисту, круг окончен (UC-09).</summary>
    private static async Task<IResult> Reject(
        Guid id, RejectRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentApproval approval,
        CancellationToken ct)
    {
        var (document, me, refusal) = await DocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;

        if (await approval.RejectAsync(document!, me, rq.Comment, ct) is { } problem) return SignRefused(document!, problem, DocumentTransitions.RejectAction);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ApprovalChanged(document!.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    private static IResult SignRefused(Document document, ApprovalProblem problem, string action) => problem switch
    {
        ApprovalProblem.NoSeat => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужое согласование",
            detail: "Документ согласует тот, кто назначен в его строке согласования."),
        ApprovalProblem.AlreadyMarked => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Отметка уже поставлена",
            detail: "Вы уже отметили этот документ — возможно, в другой вкладке."),
        ApprovalProblem.EmptyComment => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Не указан комментарий", detail: "Напишите, почему документ возвращается: комментарий увидит методист."),
        _ => Refused(document, action)
    };

    // Документ для действия согласования — с отслеживанием: его версия участвует в сохранении.
    private static async Task<(Document? Document, Guid Me, IResult? Refusal)> DocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
            return (null, default, Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Документ не найден",
                detail: "Возможно, он удалён. Вернитесь к списку документов."));
        return await directory.FindIdAsync(http.User, ct) is Guid me ? (document, me, null) : (null, default, NoDirectoryRecord());
    }

    private static IResult Refused(Document document, string action) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Действие недоступно в этом статусе",
            detail: DocumentTransitions.Refusal(document.Status, action));

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");
}

/// <summary>Документ в очереди согласующего: автор, дисциплина, тип, дата отправки на согласование.</summary>
public record ApprovalQueueItem(
    Guid Id, string Type, string DisciplineCode, string DisciplineName, string AuthorName, PlanItem? Plan, DateTimeOffset? SentAt);

/// <summary>Подписант места — запись справочника сотрудников.</summary>
public record AssignRequest(Guid EmployeeId);

/// <summary>Возврат с согласования: комментарий обязателен.</summary>
public record RejectRequest(string? Comment);

/// <summary>
/// Действие согласования выполнено: новая версия и статус документа. <paramref name="Unassigned"/> — согласование
/// не запущено: подписанты этих мест не найдены, их назначают вручную.
/// </summary>
public record ApprovalChanged(int Version, string Status, string StatusTitle, IReadOnlyList<string> Unassigned);
