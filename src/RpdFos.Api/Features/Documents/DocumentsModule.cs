using System.Security.Claims;
using System.Text.Json;
using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Plans;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Api.Features.Documents;

/// <summary>
/// Срез «Документы»: РПД по опубликованному шаблону для дисциплины плана — создание с предзаполнением, список
/// (преподаватель видит свои, FR-DOC-08), изменение по разделам, сохранение, удаление, проверка: отправка,
/// замечания, возврат и приём. Приём запускает согласование — его действия в срезе «Согласование», а состояние
/// приходит вместе с документом. Обратные переходы — возврат согласованного документа на исправление (методист) и
/// принудительная смена статуса (администратор), этап 7, A8. Архив (автор, методист, администратор) и передача документа
/// другому автору (методист, администратор), этап 7, A9. Закреплённый за дисциплиной видит чужие документы по ней только для
/// чтения, и они же — вторым списком «Документы моих дисциплин»; у дисциплины с закреплениями создают документы закреплённые
/// преподаватели, этап 7, A10. Правила, предзаполнение и проверки — в ядре
/// (<see cref="DocumentCreation"/>, <see cref="DocumentEditing"/>, <see cref="DocumentReview"/>,
/// <see cref="DocumentApproval"/>, <see cref="DocumentReopening"/>, <see cref="DocumentArchiving"/>,
/// <see cref="DocumentTransfer"/>, <see cref="DocumentTransitions"/>).
/// </summary>
public class DocumentsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/documents").WithTags("Documents").RequireAuthorization();

        g.MapGet("/", List);
        g.MapGet("/disciplines", Disciplines).RequireAuthorization(Policies.DocumentAuthors);
        g.MapGet("/assigned", Assigned);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPatch("/{id:guid}/sections/{section}", UpdateSection).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPost("/{id:guid}/sections/{section}/check", CheckSection).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPost("/{id:guid}/save", Save).RequireAuthorization(Policies.DocumentAuthors);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.DocumentAuthors);

        g.MapPost("/{id:guid}/submit", Submit).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPost("/{id:guid}/take", Take).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/{id:guid}/return", Return).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/{id:guid}/accept", Accept).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/{id:guid}/return-for-correction", ReturnForCorrection).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/{id:guid}/force-status", ForceStatus).RequireAuthorization(Policies.Admin);
        // Архив и передача документа (этап 7, A9): в архив отправляют автор, методист и администратор — чужому преподавателю
        // отказывает сама операция; передают методист и администратор.
        g.MapPost("/{id:guid}/archive", Archive).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPost("/{id:guid}/transfer", Transfer).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapGet("/{id:guid}/comments", Comments);
        g.MapPost("/{id:guid}/comments", AddComment).RequireAuthorization(Policies.Commenters);
        g.MapPost("/{id:guid}/comments/{commentId:guid}/resolve", ResolveComment).RequireAuthorization(Policies.CommentResolvers);
    }

    // Методист и администратор видят все документы, остальные — свои.
    private static bool SeesAll(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist) || user.IsInRole(AppRoles.Admin);

    // Проверяет документы методист: возвращает, принимает, оставляет замечания, правит на проверке.
    private static bool IsReviewer(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist);

    // Исправляют поля из учебного плана (этап 5.1): методист и администратор.
    private static bool IsCorrector(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist) || user.IsInRole(AppRoles.Admin);

    private static async Task<List<DocumentListItem>> List(
        string? status, Guid? planId, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var query = db.Documents.AsNoTracking();
        var me = await directory.FindIdAsync(http.User, ct);
        if (!SeesAll(http.User)) query = query.Where(d => d.AuthorId == me);
        return await ListAsync(query, status, planId, me, SeesAll(http.User), db, ct);
    }

    /// <summary>
    /// «Документы моих дисциплин» (этап 7, A10; решение 5): чужие документы дисциплин, закреплённых за пользователем, — для
    /// чтения, с теми же фильтрами, что и основной список. Свои документы в основном списке. Методист и администратор видят
    /// все документы уже в нём, поэтому здесь для них пусто, а не дубль.
    /// </summary>
    private static async Task<List<DocumentListItem>> Assigned(
        string? status, Guid? planId, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        if (SeesAll(http.User) || await directory.FindIdAsync(http.User, ct) is not Guid me) return [];
        var query = db.Documents.AsNoTracking().Where(d => d.AuthorId != me &&
            db.DisciplineAssignments.Any(a => a.EmployeeId == me && a.DisciplineId == d.DisciplineId));
        return await ListAsync(query, status, planId, me, seesAll: false, db, ct);
    }

    private static async Task<List<DocumentListItem>> ListAsync(
        IQueryable<Document> query, string? status, Guid? planId, Guid? me, bool seesAll, AppDbContext db, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(status)) query = query.Where(d => d.Status == status);

        var rows = await (
                from d in query
                join x in db.Disciplines on d.DisciplineId equals x.Id
                join a in db.Employees on d.AuthorId equals a.Id
                where planId == null || x.CurriculumPlanId == planId
                orderby d.UpdatedAt descending
                select new { Document = d, x.Code, x.Name, x.CurriculumPlanId, Author = a.ShortName })
            .ToListAsync(ct);
        var planIds = rows.Select(r => r.CurriculumPlanId).Distinct().ToList();
        var plans = await PlanItem.Query(db).Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        // Пометка «с согласования» — тем, кто разбирает возвраты (методист и администратор): так же, как плашка в
        // документе, автору она не нужна (этап 6.1, C4).
        var returned = seesAll
            ? await ReturnsFromApprovalAsync(
                db, rows.Where(r => r.Document.Status == DocumentStatuses.UnderReview).Select(r => r.Document.Id).ToList(), ct)
            : [];

        return rows.Select(r => new DocumentListItem(r.Document.Id, r.Document.Title, r.Document.Type, r.Document.Status,
            DocumentStatuses.Title(r.Document.Status), r.Document.IsOutdated, r.Code, r.Name, plans.GetValueOrDefault(r.CurriculumPlanId),
            r.Document.TemplateRevision, r.Author, r.Document.UpdatedAt, r.Document.SubmittedAt, r.Document.Version,
            // Удаляет автор свой черновик (S-10: «открыть, удалить»; этап 6, A5).
            r.Document.AuthorId == me && DocumentTransitions.CanDelete(r.Document.Status),
            returned.GetValueOrDefault(r.Document.Id))).ToList();
    }

    /// <summary>
    /// Документы на проверке, чей последний переход в этот статус пришёл с согласования (этап 6.1, C4): кто вернул и
    /// когда. Последний переход, а не любой: после возврата автору и новой отправки (или взятия методистом) последним
    /// становится другой переход, и пометка снимается.
    /// </summary>
    private static async Task<Dictionary<Guid, ReturnedFromApproval>> ReturnsFromApprovalAsync(
        AppDbContext db, List<Guid> underReview, CancellationToken ct)
    {
        if (underReview.Count == 0) return [];

        var arrivals = await (
                from h in db.DocumentStatusHistory.AsNoTracking()
                where underReview.Contains(h.DocumentId) && h.ToStatus == DocumentStatuses.UnderReview
                join e in db.Employees on h.ChangedBy equals e.Id
                select new { h.DocumentId, h.FromStatus, h.ChangedAt, e.ShortName })
            .ToListAsync(ct);
        return arrivals.GroupBy(a => a.DocumentId).Select(g => g.MaxBy(a => a.ChangedAt)!)
            .Where(a => a.FromStatus == DocumentStatuses.Approval)
            .ToDictionary(a => a.DocumentId, a => new ReturnedFromApproval(a.ShortName, a.ChangedAt));
    }

    /// <summary>
    /// Дисциплины плана шаблона, для которых создаётся документ (FR-DOC-18), и уже созданные документы по линии
    /// шаблона — для экрана «Создание документа».
    /// </summary>
    private static async Task<IResult> Disciplines(
        Guid templateId, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var template = await db.DocumentTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, ct);
        if (template?.CurriculumPlanId is not Guid planId)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Шаблон не найден",
                detail: "Возможно, он удалён. Выберите опубликованный шаблон из списка.");

        var disciplines = (await db.Disciplines.AsNoTracking().Where(d => d.CurriculumPlanId == planId)
                .OrderBy(d => d.Code).ToListAsync(ct))
            .Where(DocumentRules.CanHaveDocument).ToList();
        // Архивный документ дисциплину не занимает (этап 7, A9). Остальных у дисциплины может быть два — согласованный и
        // незавершённый (возврат на исправление, «Создать на основе», A11): показывается тот, что в работе, затем новейший.
        var existing = (await (
                    from d in db.Documents
                    join t in db.DocumentTemplates on d.TemplateId equals t.Id
                    join a in db.Employees on d.AuthorId equals a.Id
                    where t.LineageId == template.LineageId && d.Status != DocumentStatuses.Archived
                    select new { d.DisciplineId, d.Id, d.AuthorId, d.Status, d.CreatedAt, a.ShortName })
                .ToListAsync(ct))
            .OrderBy(x => DocumentTransitions.IsUnfinished(x.Status) ? 0 : 1).ThenByDescending(x => x.CreatedAt)
            .GroupBy(x => x.DisciplineId).ToDictionary(g => g.Key, g => g.First());
        var me = await directory.FindIdAsync(http.User, ct);
        var seesAll = SeesAll(http.User);

        // Закрепления дисциплин плана (этап 7, A10): за кем она закреплена, можно ли пользователю создать документ и открыть
        // чужой. Создают закреплённые преподаватели; методист и администратор — по любой дисциплине (как в самом создании).
        var holders = (await (
                    from a in db.DisciplineAssignments.AsNoTracking()
                    join x in db.Disciplines on a.DisciplineId equals x.Id
                    join e in db.Employees on a.EmployeeId equals e.Id
                    where x.CurriculumPlanId == planId
                    orderby e.ShortName
                    select new { a.DisciplineId, Holder = new AssignmentHolder(e.Id, e.ShortName) })
                .ToListAsync(ct))
            .GroupBy(x => x.DisciplineId).ToDictionary(g => g.Key, g => g.Select(x => x.Holder).ToList());

        return Results.Ok(disciplines.Select(d =>
        {
            var assigned = holders.GetValueOrDefault(d.Id);
            var isHolder = me is Guid employeeId && assigned?.Any(h => h.EmployeeId == employeeId) == true;
            var (assignedTo, canCreate) = (assigned is null ? null : DisciplineAssignments.Names(assigned), seesAll || assigned is null || isHolder);
            return existing.TryGetValue(d.Id, out var doc)
                ? new DisciplineOption(d.Id, d.Code, d.Name, doc.Id, doc.ShortName, seesAll || doc.AuthorId == me || isHolder, assignedTo, canCreate)
                : new DisciplineOption(d.Id, d.Code, d.Name, null, null, false, assignedTo, canCreate);
        }).ToList());
    }

    private static async Task<IResult> Get(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, IDocumentValidator validator,
        ValidationRuleSet rules, DocumentTypeRegistry registry, DocumentApproval approval, DisciplineAssignments assignments,
        CancellationToken ct)
    {
        var (document, viewer, refusal) = await VisibleDocumentAsync(id, http, db, directory, approval, assignments, ct);
        if (refusal is not null) return refusal;
        return Results.Ok(await DetailsAsync(document!, viewer!, db, validator, rules, registry, approval, ct));
    }

    private static async Task<IResult> Create(
        CreateDocumentRequest rq, HttpContext http, DirectoryAccounts directory, DocumentCreation creation, AppDbContext db,
        IDocumentValidator validator, ValidationRuleSet rules, DocumentTypeRegistry registry, DocumentApproval approval,
        CancellationToken ct)
    {
        // Автор — запись справочника текущего пользователя: она же первый составитель.
        var authorId = await directory.FindIdAsync(http.User, ct);
        if (authorId is null) return NoDirectoryRecord();

        // Закрепления дисциплин ограничивают преподавателей; методист и администратор создают «за кафедру» (этап 7, A10).
        var result = await creation.CreateAsync(rq.TemplateId, rq.DisciplineId, authorId.Value, bypassAssignments: IsCorrector(http.User), ct);
        return result switch
        {
            { Document: { } document } => Results.Created($"/api/documents/{document.Id}",
                await DetailsAsync(document,
                    new DocumentViewer(authorId, IsAuthor: true, IsReviewer(http.User), IsCorrector(http.User), IsSignatory: false,
                        IsAdmin: http.User.IsInRole(AppRoles.Admin)),
                    db, validator, rules, registry, approval, ct)),
            { Problem: DocumentCreationProblem.Duplicate } => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Документ уже есть", detail: result.Detail,
                extensions: new Dictionary<string, object?>
                {
                    ["documentId"] = result.ExistingId, ["location"] = $"/api/documents/{result.ExistingId}"
                }),
            // Доступ, а не данные запроса: дисциплину закрепили за другими преподавателями (этап 7, A10).
            { Problem: DocumentCreationProblem.DisciplineAssigned } => Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Дисциплина закреплена", detail: result.Detail),
            _ => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: result.Problem switch
                {
                    DocumentCreationProblem.TemplateNotFound => "Шаблон не найден",
                    DocumentCreationProblem.TemplateNotPublished => "Шаблон не опубликован",
                    DocumentCreationProblem.TypeNotSupported => "Тип документа пока не создаётся",
                    DocumentCreationProblem.DisciplineNotFound => "Дисциплина не найдена",
                    DocumentCreationProblem.DisciplineNotInPlan => "Дисциплина не из плана шаблона",
                    _ => "Для этой строки плана документ не создаётся"
                },
                detail: result.Detail)
        };
    }

    /// <summary>
    /// Изменение раздела — автосохранение и уход из раздела: проверка черновика, санитизация, запрет полей из плана,
    /// синхронизация нормализованных таблиц. Правит автор или методист на проверке. Ответ — новая версия и
    /// заполненность раздела.
    /// </summary>
    // Кто и в каком объёме правит раздел: автор — своё содержимое, методист на проверке — всё, после приёма методист
    // и администратор — поля из плана. Те же права — у проверки раздела без сохранения.
    private static async Task<(Document? Document, Guid EditorId, DocumentEditScope Scope, IResult? Refusal)> SectionEditorAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, default, default, NotFound());
        var editorId = await directory.FindIdAsync(http.User, ct);
        if (editorId is null) return (null, default, default, NoDirectoryRecord());
        var isAuthor = document.AuthorId == editorId;
        var scope = DocumentTransitions.EditScope(document.Status, isAuthor, IsReviewer(http.User), IsCorrector(http.User));
        if (scope is null)
            return (null, default, default, isAuthor || IsCorrector(http.User)
                ? await AuthorRefusedAsync(document, isAuthor, review, DocumentTransitions.EditAction, ct)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Изменяет только автор",
                    detail: "Документ изменяет его автор, а на проверке — методист."));
        return (document, editorId.Value, scope.Value, null);
    }

    /// <summary>
    /// Проверка раздела без сохранения (этап 5.2, B4.1): редактор присылает содержимое раздела при уходе из поля и
    /// получает состояние разделов — рамки и индикаторы обновляются сразу. Документ, версия и история не меняются;
    /// ошибки черновика — в ответе.
    /// </summary>
    private static async Task<IResult> CheckSection(
        Guid id, string section, SectionUpdateRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, DocumentReview review, CancellationToken ct)
    {
        var (document, editorId, scope, refusal) = await SectionEditorAsync(id, http, db, directory, review, ct);
        if (refusal is not null) return refusal;

        var result = await editing.UpdateSectionAsync(document!, section, rq.Value, editorId, ct, scope, checkOnly: true);
        return result.Sections is { } sections
            ? Results.Ok(new SectionChecked(sections, result.HiddenPaths ?? [], result.Errors))
            : SectionProblem(section, result);
    }

    private static async Task<IResult> UpdateSection(
        Guid id, string section, SectionUpdateRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, DocumentReview review, TimeProvider clock, CancellationToken ct)
    {
        var (document, editorId, scope, refusal) = await SectionEditorAsync(id, http, db, directory, review, ct);
        if (refusal is not null) return refusal;

        // Сравнивать будем с версией, которую видел клиент; новую проставит перехватчик сохранения.
        db.Entry(document!).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await editing.UpdateSectionAsync(document!, section, rq.Value, editorId, ct, scope, rq.ConfirmReturn);
        if (result.Problem is not null) return SectionProblem(section, result);

        await db.SaveChangesAsync(ct);
        // Состояние всех разделов и история исправлений — редактор обновляет их сразу, без перезагрузки документа.
        return Results.Ok(new SectionSaved(document!.Version, document.Status, DocumentStatuses.Title(document.Status),
            result.Section!, clock.GetUtcNow(), result.Sections,
            scope == DocumentEditScope.Content ? null : await CorrectionsAsync(db, document.Id, ct), result.HiddenPaths, result.Payload));
    }

    private static IResult SectionProblem(string section, SectionUpdateResult result)
    {
        var problem = result.Problem!.Value;
        return problem switch
            {
                SectionUpdateProblem.ReturnNotConfirmed => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Исправление вернёт документ автору",
                    detail: "После исправления документ не проходит проверку: " +
                        string.Join(" ", result.Errors.Select(e => $"{e.Path}: {e.Message}.")) +
                        " Подтвердите возврат на доработку (confirmReturn) или отмените исправление.",
                    extensions: new Dictionary<string, object?> { ["errors"] = result.Errors, ["confirmReturn"] = true }),
                SectionUpdateProblem.UnknownSection => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                    title: "Раздел не найден", detail: $"Раздела «{section}» нет в структуре документа."),
                SectionUpdateProblem.HiddenSection => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Раздел не предусмотрен учебным планом", detail: "Для дисциплины этот раздел не заполняется."),
                _ => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: problem == SectionUpdateProblem.ReadOnlyChanged ? "Поля из учебного плана не изменяются" : "Раздел заполнен с ошибками",
                    detail: string.Join(" ", result.Errors.Select(e => $"{e.Path}: {e.Message}.")),
                    extensions: new Dictionary<string, object?> { ["errors"] = result.Errors })
            };
    }

    private static async Task<IResult> Save(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, DocumentReview review, TimeProvider clock, CancellationToken ct)
    {
        var (document, editorId, refusal) = await EditorDocumentAsync(id, http, db, directory, review, DocumentTransitions.SaveAction, ct);
        if (refusal is not null) return refusal;

        db.Entry(document!).Property(d => d.Version).OriginalValue = rq.Version;
        await editing.SaveAsync(document!, editorId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentSaved(document!.Version, document.Status, DocumentStatuses.Title(document.Status), clock.GetUtcNow()));
    }

    private static async Task<IResult> Delete(
        Guid id, int version, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentEditing editing,
        CancellationToken ct)
    {
        var (document, editorId, refusal) = await AuthorDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (!DocumentTransitions.CanDelete(document!.Status)) return Refused(document, DocumentTransitions.DeleteAction);

        db.Entry(document).Property(d => d.Version).OriginalValue = version;
        editing.Delete(document, editorId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Отправка на проверку (и повторная — с доработки): полная проверка — схема в режиме отправки, условные разделы,
    /// правила. Ошибки — 422 со списком; предупреждения отправку не блокируют и приходят в ответе.
    /// </summary>
    private static async Task<IResult> Submit(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentReview review, CancellationToken ct)
    {
        var (document, authorId, refusal) = await AuthorDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (!DocumentTransitions.CanSubmit(document!.Status))
            return await AuthorRefusedAsync(document, isAuthor: true, review, DocumentTransitions.SubmitAction, ct);

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await review.SubmitAsync(document, authorId, ct);
        if (!result.Submitted)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Документ не прошёл проверку",
                detail: "Исправьте ошибки и отправьте документ снова.",
                extensions: new Dictionary<string, object?> { ["errors"] = result.Issues });

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), result.Issues));
    }

    /// <summary>
    /// «Взять на проверку» (этап 6, A1): методист переводит документ из статусов автора в «На проверке» с
    /// обязательной причиной. Полной проверки нет — её требует приём.
    /// </summary>
    private static async Task<IResult> Take(
        Guid id, TakeRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review,
        CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        var reviewerId = await directory.FindIdAsync(http.User, ct);
        if (reviewerId is null) return NoDirectoryRecord();
        if (!DocumentTransitions.CanTakeForReview(document.Status)) return Refused(document, DocumentTransitions.TakeAction);

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        if (await review.TakeAsync(document, reviewerId.Value, rq.Reason, ct) is not null)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Не указана причина",
                detail: "Напишите, почему документ берётся на проверку: причина попадёт в историю статусов.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    private static async Task<IResult> Return(
        Guid id, ReturnRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review,
        CancellationToken ct)
    {
        var (document, reviewerId, refusal) = await ReviewedDocumentAsync(id, http, db, directory, DocumentTransitions.ReturnAction, ct);
        if (refusal is not null) return refusal;

        db.Entry(document!).Property(d => d.Version).OriginalValue = rq.Version;
        if (await review.ReturnAsync(document!, reviewerId, rq.Comment, ct) is not null)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Нет неразрешённых замечаний",
                detail: "Вернуть на доработку можно с замечаниями: оставьте замечание к полю, которое нужно исправить.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document!.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    private static async Task<IResult> Accept(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review,
        CancellationToken ct)
    {
        var (document, reviewerId, refusal) = await ReviewedDocumentAsync(id, http, db, directory, DocumentTransitions.AcceptAction, ct);
        if (refusal is not null) return refusal;

        db.Entry(document!).Property(d => d.Version).OriginalValue = rq.Version;
        // Приём запускает согласование сам; не нашлись подписанты — документ остаётся «Принят» (этап 6, B2).
        var (problem, issues, _) = await review.AcceptAsync(document!, reviewerId, ct);
        if (problem == ReviewProblem.UnresolvedComments)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Есть неразрешённые замечания",
                detail: "Документ с неразрешёнными замечаниями не принимается — верните его на доработку.");
        // Как при отправке: принимается только документ без ошибок полной проверки (этап 5.2).
        if (problem == ReviewProblem.CheckFailed)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Документ не прошёл проверку",
                detail: "Исправьте ошибки или оставьте замечание и верните документ на доработку.",
                extensions: new Dictionary<string, object?> { ["errors"] = issues });

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document!.Version, document.Status, DocumentStatuses.Title(document.Status), issues));
    }

    /// <summary>
    /// «Вернуть на исправление» (этап 7, A8, решение 2): методист возвращает согласованный документ на проверку. Причина
    /// обязательна — 422; другой незавершённый документ той же дисциплины и линии шаблона — 409.
    /// </summary>
    private static async Task<IResult> ReturnForCorrection(
        Guid id, CorrectionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReopening reopening,
        CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();
        if (!DocumentTransitions.CanReturnForCorrection(document.Status))
            return Refused(document, DocumentTransitions.ReturnForCorrectionAction);

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await reopening.ReturnForCorrectionAsync(document, me, rq.Reason, ct);
        if (result.Problem is not null)
            return ReopeningRefused(result, "Не указана причина",
                "Напишите, почему согласованный документ возвращается на исправление: причина попадёт в историю статусов и в уведомление.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    /// <summary>
    /// «Сменить статус» (этап 7, A8; FR-ADM-04): администратор принудительно переводит документ назад — в «Сохранённый
    /// черновик», «На доработке» или «На проверке». Комментарий обязателен — 422, как и недопустимый статус; тот же статус и
    /// другой незавершённый документ дисциплины — 409. Удалённый документ из выборки исключён (404).
    /// </summary>
    private static async Task<IResult> ForceStatus(
        Guid id, ForceStatusRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReopening reopening,
        CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await reopening.ForceStatusAsync(document, me, rq.Status, rq.Comment, ct);
        if (result.Problem is not null)
            return ReopeningRefused(result, "Не указан комментарий",
                "Принудительная смена статуса — для нештатных ситуаций: напишите, что произошло. Комментарий попадёт в историю статусов и журнал аудита.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    // Отказ обратного перехода (этап 7, A8). Данные запроса — 422, состояние документа — 409; текст пустого поля зависит от перехода.
    private static IResult ReopeningRefused(ReopeningResult result, string emptyTitle, string emptyDetail) => result.Problem switch
    {
        ReopeningProblem.EmptyText => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: emptyTitle, detail: emptyDetail),
        ReopeningProblem.TargetNotAllowed => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Статус недоступен",
            detail: "Принудительно документ переводят в «Сохранённый черновик», «На доработке» или «На проверке»."),
        ReopeningProblem.SameStatus => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Статус не изменится",
            detail: "Документ уже в этом статусе."),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Есть незавершённый документ",
            detail: "У дисциплины уже есть другой незавершённый документ по этому шаблону: пока он не согласован, не архивирован и не " +
                    "удалён, этот в работу вернуть нельзя.",
            extensions: new Dictionary<string, object?>
            {
                ["documentId"] = result.OtherId, ["location"] = $"/api/documents/{result.OtherId}"
            })
    };

    /// <summary>
    /// «В архив» (этап 7, A9, решение 3): согласованный документ отправляют в архив автор, методист или администратор.
    /// Преподаватель, не автор, — 403 раньше проверки статуса: чужой документ не сообщает, в каком он состоянии. Документ в
    /// другом статусе — 409. Подтверждение — на клиенте.
    /// </summary>
    private static async Task<IResult> Archive(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentArchiving archiving,
        CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();
        if (document.AuthorId != me && !IsCorrector(http.User))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужой документ",
                detail: "В архив документ отправляет его автор, методист или администратор.");
        if (!DocumentTransitions.CanArchive(document.Status)) return Refused(document, DocumentTransitions.ArchiveAction);

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        await archiving.ArchiveAsync(document, me, ActorRole(http.User), ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    /// <summary>
    /// «Передать документ» (этап 7, A9; FR-DOC-15): методист или администратор передаёт документ любого статуса, кроме
    /// удалённого (404), другому автору. Причина и новый автор с действующей учётной записью преподавателя — 422; тот же автор — 409.
    /// </summary>
    private static async Task<IResult> Transfer(
        Guid id, TransferRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentTransfer transfer,
        CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await transfer.TransferAsync(document, me, ActorRole(http.User), rq.EmployeeId, rq.Reason, ct);
        if (result.Problem is { } problem) return TransferRefused(problem);

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    private static IResult TransferRefused(TransferProblem problem) => problem switch
    {
        TransferProblem.EmptyReason => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Не указана причина",
            detail: "Напишите, почему документ передаётся другому автору: причина попадёт в журнал аудита и в уведомления."),
        TransferProblem.EmployeeNotFound => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Сотрудник не найден", detail: "Выберите нового автора в справочнике: запись должна быть действующей."),
        TransferProblem.NoTeacherAccount => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "У сотрудника нет учётной записи преподавателя",
            detail: "Новый автор входит в систему и правит документ как преподаватель: создайте или включите ему учётную запись " +
                    "с ролью «Преподаватель» на странице «Пользователи»."),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Автор не изменится",
            detail: "Документ уже у этого автора. Выберите другого сотрудника.")
    };

    // Роль, от которой действует пользователь, — для текста уведомлений: методист назван раньше администратора, а автор без этих
    // ролей отправляет в архив собственный документ и уведомления не создаёт.
    private static string ActorRole(ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.Methodist) ? AppRoles.Methodist : user.IsInRole(AppRoles.Admin) ? AppRoles.Admin : AppRoles.Teacher;

    /// <summary>Замечания к документу — всем, кто его видит: от старых к новым.</summary>
    private static async Task<IResult> Comments(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentApproval approval,
        DisciplineAssignments assignments, CancellationToken ct)
    {
        var (_, _, refusal) = await VisibleDocumentAsync(id, http, db, directory, approval, assignments, ct);
        if (refusal is not null) return refusal;
        return Results.Ok(await CommentItem.Query(db, db.DocumentComments.Where(c => c.DocumentId == id)).ToListAsync(ct));
    }

    /// <summary>
    /// Замечание к полю: методист — на проверке, согласующий — на согласовании, пока его строка ждёт отметки (этап 7,
    /// A2). Порядок отказов согласующему — как у отметки: чужой документ (403), не тот статус, отметка уже стоит (409).
    /// </summary>
    private static async Task<IResult> AddComment(
        Guid id, CommentRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review,
        DocumentApproval approval, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();

        var isReviewer = IsReviewer(http.User);
        var (seat, awaiting) = await SeatAsync(http.User, id, me, approval, ct);
        if (!DocumentTransitions.CanComment(document.Status, isReviewer, awaiting))
        {
            if (!isReviewer && !seat)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужое согласование",
                    detail: "На согласовании замечания оставляет тот, кто назначен в строке согласования документа.");
            return seat && DocumentTransitions.CanSign(document.Status)
                ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Отметка уже поставлена",
                    detail: "Замечания оставляют до своей отметки: вы уже согласовали документ.")
                : Refused(document, DocumentTransitions.CommentAction);
        }

        var (comment, problem) = await review.CommentAsync(document, me, rq.FieldPath ?? "", rq.Text ?? "", ct);
        if (problem is not null)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: problem == ReviewProblem.EmptyText ? "Пустое замечание" : "Поле не найдено",
                detail: problem == ReviewProblem.EmptyText
                    ? "Напишите, что нужно исправить."
                    : $"Места «{rq.FieldPath}» нет в структуре документа.");

        await db.SaveChangesAsync(ct);
        var item = await CommentItem.Query(db, db.DocumentComments.Where(c => c.Id == comment!.Id)).SingleAsync(ct);
        return Results.Created($"/api/documents/{id}/comments/{comment!.Id}", item);
    }

    /// <summary>
    /// Замечание отмечает разрешённым автор, пока документ на доработке; методист на проверке — своё (этап 6, A2) и
    /// оставленное на согласовании; согласующий — своё, пока его строка ждёт отметки (этап 7, A2).
    /// </summary>
    private static async Task<IResult> ResolveComment(
        Guid id, Guid commentId, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentApproval approval,
        TimeProvider clock, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (await directory.FindIdAsync(http.User, ct) is not Guid me) return NoDirectoryRecord();
        var comment = await db.DocumentComments.SingleOrDefaultAsync(c => c.Id == commentId && c.DocumentId == id, ct);
        if (comment is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Замечание не найдено",
                detail: "Обновите страницу документа.");

        var (isAuthor, isReviewer, own) = (document.AuthorId == me, IsReviewer(http.User), comment.AuthorId == me);
        var (seat, awaiting) = await SeatAsync(http.User, id, me, approval, ct);
        // Чьё замечание — нужно только методисту на проверке: чужое он снимает, если оно оставлено на согласовании.
        var fromApproval = isReviewer && !own && DocumentTransitions.CanReview(document.Status) &&
                           await DocumentReview.FromApproval(db).ContainsAsync(commentId, ct);
        if (!DocumentTransitions.CanResolveComment(document.Status, isAuthor, isReviewer, awaiting, own, fromApproval))
        {
            if (isReviewer && DocumentTransitions.CanReview(document.Status))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужое замечание",
                    detail: "Методист снимает свои замечания и замечания согласующих; замечание другого методиста разрешает автор на доработке.");
            if (seat && !own && DocumentTransitions.CanSign(document.Status))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужое замечание",
                    detail: "Согласующий снимает только свои замечания.");
            return isAuthor || isReviewer || seat
                ? Refused(document, DocumentTransitions.ResolveAction)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужой документ",
                    detail: "Замечания разрешает автор документа, а свои — методист на проверке и согласующий на согласовании.");
        }
        if (!comment.Resolve(me, clock.GetUtcNow()))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Замечание уже разрешено",
                detail: "Его отметили раньше — возможно, в другой вкладке.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(await CommentItem.Query(db, db.DocumentComments.Where(c => c.Id == commentId)).SingleAsync(ct));
    }

    // Документ видят автор, методист, администратор, согласующий со строкой согласования в нём (этап 6, B1) и сотрудник,
    // за которым закреплена дисциплина документа (этап 7, A10). Закреплённый — последним: запрос к базе только тем, кто
    // иначе документа не видит.
    private static async Task<(Document? Document, DocumentViewer? Viewer, IResult? Refusal)> VisibleDocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentApproval approval,
        DisciplineAssignments assignments, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, null, NotFound());
        var me = await directory.FindIdAsync(http.User, ct);
        var viewer = await DocumentViewer.OfAsync(http.User, me, document, approval, ct);
        if (!viewer.Sees && me is Guid employeeId && await assignments.IsAssignedAsync(document.DisciplineId, employeeId, ct))
            viewer = viewer with { IsAssigned = true };
        if (!viewer.Sees)
            return (null, null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужой документ",
                detail: "Документ видят его автор, методист, администратор, назначенные согласующие и сотрудники, " +
                        "за которыми закреплена дисциплина."));
        return (document, viewer, null);
    }

    // Удаляет документ, отправляет его и разрешает замечания только автор.
    private static async Task<(Document? Document, Guid EditorId, IResult? Refusal)> AuthorDocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, default, NotFound());
        var me = await directory.FindIdAsync(http.User, ct);
        if (me is null) return (null, default, NoDirectoryRecord());
        if (document.AuthorId != me)
            return (null, default, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Изменяет только автор",
                detail: "Документ изменяет, отправляет и удаляет его автор."));
        return (document, me.Value, null);
    }

    // Строка согласования пользователя в документе: есть ли и ждёт ли отметки. Без роли «Утверждающий» назначение
    // согласующим ничего не даёт — как и для доступа к документу (этап 6, B1).
    private static async Task<(bool Seat, bool Awaiting)> SeatAsync(
        ClaimsPrincipal user, Guid documentId, Guid me, DocumentApproval approval, CancellationToken ct) =>
        user.IsInRole(AppRoles.Approver) ? await approval.SeatOfAsync(documentId, me, ct) : (false, false);

    // Возвращает и принимает методист — пока документ на проверке.
    private static async Task<(Document? Document, Guid ReviewerId, IResult? Refusal)> ReviewedDocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, string action, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, default, NotFound());
        var me = await directory.FindIdAsync(http.User, ct);
        if (me is null) return (null, default, NoDirectoryRecord());
        if (!DocumentTransitions.CanReview(document.Status)) return (null, default, Refused(document, action));
        return (document, me.Value, null);
    }

    /// <summary>
    /// Правка содержимого и «Сохранить»: автор — черновик и доработка; методист — документ на проверке
    /// (FR-REV-11). Документ в другом статусе — 409, чужой — 403.
    /// </summary>
    private static async Task<(Document? Document, Guid EditorId, IResult? Refusal)> EditorDocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review, string action,
        CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, default, NotFound());
        var me = await directory.FindIdAsync(http.User, ct);
        if (me is null) return (null, default, NoDirectoryRecord());

        var isAuthor = document.AuthorId == me;
        var isReviewer = IsReviewer(http.User);
        if (isAuthor && DocumentTransitions.CanEdit(document.Status, DocumentRole.Author) ||
            isReviewer && DocumentTransitions.CanEdit(document.Status, DocumentRole.Reviewer))
            return (document, me.Value, null);
        if (isAuthor || isReviewer) return (null, default, await AuthorRefusedAsync(document, isAuthor, review, action, ct));
        return (null, default, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Изменяет только автор",
            detail: "Документ изменяет его автор, а на проверке — методист."));
    }

    private static async Task<DocumentDetails> DetailsAsync(
        Document document, DocumentViewer viewer, AppDbContext db, IDocumentValidator validator, ValidationRuleSet rules,
        DocumentTypeRegistry registry, DocumentApproval approval, CancellationToken ct)
    {
        var (isAuthor, isReviewer, isCorrector) = (viewer.IsAuthor, viewer.IsReviewer, viewer.IsCorrector);
        var template = await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId)
            .Select(t => new { t.Schema, t.DefaultPayload, t.PrintMapping }).SingleAsync(ct);
        var schemaJson = template.Schema;
        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        var payload = JsonDocument.Parse(document.Payload).RootElement;
        var status = document.Status;
        var scope = DocumentTransitions.EditScope(status, isAuthor, isReviewer, isCorrector);
        var corrections = await CorrectionsAsync(db, document.Id, ct);
        var panel = await ApprovalAsync(document, viewer, approval, ct);
        // Строка согласующего ждёт отметки: он согласует или возвращает документ и оставляет замечания (этап 7, A2).
        var awaiting = viewer.IsSignatory && panel is not null && panel.Seats.Any(s => s.Mine && s.Status == SignatureStatuses.Pending);
        var signs = awaiting && DocumentTransitions.CanSign(status);

        return new DocumentDetails(
            DocumentItem.From(document, facts.Discipline),
            payload,
            JsonDocument.Parse(schemaJson).RootElement,
            DocumentMetadata.Build(schemaJson, payload, status, facts, validator, rules, registry.FindSchema,
                template.PrintMapping is { } mapping ? PrintMappingJson.ReadMapping(mapping) : null),
            // Остальным редактор показывает документ только для чтения.
            isAuthor && DocumentTransitions.CanEdit(status, DocumentRole.Author) ||
            isReviewer && DocumentTransitions.CanEdit(status, DocumentRole.Reviewer),
            new DocumentActions(
                Submit: isAuthor && DocumentTransitions.CanSubmit(status),
                Review: isReviewer && DocumentTransitions.CanReview(status),
                ResolveComments: isAuthor && DocumentTransitions.CanResolveComments(status),
                Print: DocumentTransitions.CanPrint(status, isAuthor, isReviewer, viewer.IsSignatory),
                CorrectPlan: scope is DocumentEditScope.ContentAndPlan or DocumentEditScope.PlanOnly,
                TakeForReview: isReviewer && DocumentTransitions.CanTakeForReview(status),
                ResolveOwnComments: isReviewer && DocumentTransitions.CanReview(status) || signs,
                Delete: isAuthor && DocumentTransitions.CanDelete(status),
                Approve: signs,
                Comment: DocumentTransitions.CanComment(status, isReviewer, awaiting),
                ResolveApprovalComments: isReviewer && DocumentTransitions.CanReview(status),
                ReturnForCorrection: isReviewer && DocumentTransitions.CanReturnForCorrection(status),
                ForceTargets: viewer.IsAdmin
                    ? DocumentTransitions.ForceTargetsFrom(status).Select(s => new StatusOption(s, DocumentStatuses.Title(s))).ToList()
                    : null,
                Archive: (isAuthor || isCorrector) && DocumentTransitions.CanArchive(status),
                Transfer: isCorrector && DocumentTransitions.CanTransfer(status)),
            template.DefaultPayload is { } defaults ? JsonDocument.Parse(defaults).RootElement : null,
            corrections, viewer.Id, panel);
    }

    /// <summary>
    /// Панель «Согласование»: с приёма и до согласованного документа, а после — в архивном (этап 7, A9: круг, который его
    /// согласовал, остаётся виден) — всем, кто видит документ. После возврата с согласования строки прежнего круга и плашку
    /// «кто, когда, почему» видят те, кто разбирает возврат, — методист и администратор; автору они не нужны: это не
    /// замечания к полям. Им же — плашка «возвращён на исправление» (этап 7, A8), пока документ не принят заново.
    /// </summary>
    private static async Task<ApprovalPanel?> ApprovalAsync(
        Document document, DocumentViewer viewer, DocumentApproval approval, CancellationToken ct)
    {
        var status = document.Status;
        var active = status is DocumentStatuses.Accepted or DocumentStatuses.Approval or DocumentStatuses.Approved
            or DocumentStatuses.Archived;
        if (!active && !viewer.IsCorrector) return null;
        var state = await approval.StateAsync(document, ct);
        if (!active && state.Returned is null && state.Corrected is null) return null;

        return new ApprovalPanel(
            state.Seats.Select(s => new ApprovalSeatItem(s.Role, s.Title, s.SignatoryId, s.SignatoryName, s.Position, s.Status,
                SignatureStatuses.Title(s.Status), s.MarkedAt, s.Comment, s.CanSign, s.SignatoryId is Guid id && id == viewer.Id)).ToList(),
            CanAssign: viewer.IsCorrector && DocumentTransitions.CanAssignSignatory(status) && state.Seats.Count > 0,
            // Строк нет — документ принят до появления согласования: запуск сам определит подписантов.
            CanStart: viewer.IsReviewer && DocumentTransitions.CanStartApproval(status) && state.Seats.All(s => s.SignatoryId is not null),
            Returned: viewer.IsCorrector ? state.Returned : null,
            Corrected: viewer.IsCorrector ? state.Corrected : null);
    }

    // Исправления полей из плана — из журнала аудита: кто, когда, было, стало.
    private static async Task<List<PlanCorrection>> CorrectionsAsync(AppDbContext db, Guid documentId, CancellationToken ct) =>
        (await (
                from a in db.AuditLog
                where a.EntityType == AuditEntities.Document && a.EntityId == documentId && a.EventType == AuditEvents.PlanFieldCorrected
                join e in db.Employees on a.UserId equals e.Id into editors
                from e in editors.DefaultIfEmpty()
                orderby a.OccurredAt
                select new { a.Payload, a.OccurredAt, Name = e == null ? null : e.ShortName })
            .ToListAsync(ct))
        .Select(c => PlanCorrection.From(c.Payload, c.Name, c.OccurredAt)).ToList();

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Документ не найден",
            detail: "Возможно, он удалён в другой вкладке. Вернитесь к списку документов.");

    public const string TakenByReviewer = "Документ взят на проверку методистом";

    // Отказ автору: документ, который у него открыт, взял на проверку методист, — понятным текстом (этап 6, A1).
    private static async Task<IResult> AuthorRefusedAsync(
        Document document, bool isAuthor, DocumentReview review, string action, CancellationToken ct) =>
        isAuthor && await review.TakenByReviewerAsync(document, ct)
            ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: TakenByReviewer,
                detail: "Изменения не сохранены: документ проверяет методист. Скопируйте введённое и перезагрузите документ.")
            : Refused(document, action);

    private static IResult Refused(Document document, string action) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Действие недоступно в этом статусе",
            detail: DocumentTransitions.Refusal(document.Status, action));

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");
}

public record CreateDocumentRequest(Guid TemplateId, Guid DisciplineId);

/// <summary>Значение раздела целиком и версия документа, которую видел клиент.</summary>
/// <param name="ConfirmReturn">Исправляющий подтвердил возврат принятого документа автору, если исправление сделает его неполным.</param>
public record SectionUpdateRequest(int Version, JsonElement Value, bool ConfirmReturn = false);

public record DocumentVersionRequest(int Version);

/// <summary>«Взять на проверку»: версия и обязательная причина — в историю статусов.</summary>
public record TakeRequest(int Version, string? Reason);

/// <summary>Возврат на доработку: версия и необязательный общий комментарий — замечания к полям уже есть.</summary>
public record ReturnRequest(int Version, string? Comment);

/// <summary>«Вернуть на исправление» (этап 7, A8): версия и обязательная причина — в историю статусов и в уведомление.</summary>
public record CorrectionRequest(int Version, string? Reason);

/// <summary>«Сменить статус» (этап 7, A8): версия, целевой статус и обязательный комментарий — в историю статусов и аудит.</summary>
public record ForceStatusRequest(int Version, string? Status, string? Comment);

/// <summary>«Передать документ» (этап 7, A9): версия, запись справочника нового автора и обязательная причина — в аудит и уведомления.</summary>
public record TransferRequest(int Version, Guid EmployeeId, string? Reason);

/// <summary>Замечание: JSON Pointer поля и текст.</summary>
public record CommentRequest(string? FieldPath, string? Text);

/// <summary>Раздел проверен без сохранения: состояние всех разделов по присланному и ошибки черновика.</summary>
public record SectionChecked(IReadOnlyList<SectionState> Sections, IReadOnlyList<string> HiddenPaths,
    IReadOnlyList<DocumentValidationError> Errors);

/// <summary>Раздел сохранён: новая версия, статус (после «Сохранить» правка возвращает черновик) и заполненность раздела.</summary>
public record SectionSaved(int Version, string Status, string StatusTitle, SectionState Section, DateTimeOffset SavedAt,
    IReadOnlyList<SectionState>? Sections = null, IReadOnlyList<PlanCorrection>? PlanCorrections = null,
    IReadOnlyList<string>? HiddenPaths = null, JsonElement? Payload = null);

public record DocumentSaved(int Version, string Status, string StatusTitle, DateTimeOffset SavedAt);

/// <summary>Статус сменился: новая версия и статус; при отправке — предупреждения полной проверки.</summary>
public record DocumentReviewed(int Version, string Status, string StatusTitle, IReadOnlyList<DocumentValidationError> Warnings);

/// <param name="FromApproval">Документ на проверке вернулся с согласования — методисту и администратору (этап 6.1, C4).</param>
public record DocumentListItem(
    Guid Id, string Title, string Type, string Status, string StatusTitle, bool IsOutdated, string DisciplineCode,
    string DisciplineName, PlanItem? Plan, int TemplateRevision, string AuthorName, DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt, int Version = 0, bool CanDelete = false, ReturnedFromApproval? FromApproval = null);

/// <summary>Кто и когда вернул документ с согласования на проверку: пометка «с согласования» в очереди «Проверка».</summary>
public record ReturnedFromApproval(string By, DateTimeOffset At);

/// <summary>
/// Дисциплина плана для создания документа; есть документ — ссылка на него, если он доступен пользователю.
/// <paramref name="AssignedTo"/> — за кем дисциплина закреплена (этап 7, A10; null — ни за кем), <paramref name="CanCreate"/> —
/// может ли создать документ пользователь: закреплений нет, он закреплён или это методист или администратор.
/// </summary>
public record DisciplineOption(
    Guid Id, string Code, string Name, Guid? DocumentId, string? DocumentAuthor, bool CanOpen, string? AssignedTo = null,
    bool CanCreate = true);

public record DocumentItem(
    Guid Id, string Type, string Title, string Status, string StatusTitle, bool IsOutdated, int Version, Guid TemplateId,
    int TemplateRevision, Guid DisciplineId, string DisciplineCode, string DisciplineName, Guid AuthorId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SubmittedAt)
{
    public static DocumentItem From(Document d, Discipline discipline) =>
        new(d.Id, d.Type, d.Title, d.Status, DocumentStatuses.Title(d.Status), d.IsOutdated, d.Version, d.TemplateId,
            d.TemplateRevision, d.DisciplineId, discipline.Code, discipline.Name, d.AuthorId, d.CreatedAt, d.UpdatedAt, d.SubmittedAt);
}

/// <summary>
/// Что пользователь может сделать с документом сейчас: отправить, проверить (вернуть, принять, замечания), разрешать
/// замечания, формировать печатную форму, исправлять поля из учебного плана (методист и администратор, этап 5.1).
/// </summary>
/// <param name="TakeForReview">Методист берёт документ на проверку из статусов автора (этап 6, A1).</param>
/// <param name="ResolveOwnComments">
/// Свои замечания снимают методист на проверке (этап 6, A2) и согласующий, пока его строка ждёт отметки (этап 7, A2).
/// </param>
/// <param name="Delete">Автор удаляет свой черновик (этап 6, A5).</param>
/// <param name="Approve">Согласующий отмечает согласие или возвращает документ на проверку (этап 6, B3).</param>
/// <param name="Comment">Оставить замечание к полю: методист на проверке, согласующий до своей отметки (этап 7, A2).</param>
/// <param name="ResolveApprovalComments">Методист на проверке снимает и замечания, оставленные на согласовании (этап 7, A2).</param>
/// <param name="ReturnForCorrection">Методист возвращает согласованный документ на исправление (этап 7, A8).</param>
/// <param name="ForceTargets">
/// Статусы, в которые администратор переводит документ принудительно (этап 7, A8; FR-ADM-04); null — не администратор.
/// </param>
/// <param name="Archive">Автор, методист или администратор отправляют согласованный документ в архив (этап 7, A9).</param>
/// <param name="Transfer">Методист или администратор передают документ другому автору (этап 7, A9; FR-DOC-15).</param>
public record DocumentActions(bool Submit, bool Review, bool ResolveComments, bool Print, bool CorrectPlan = false,
    bool TakeForReview = false, bool ResolveOwnComments = false, bool Delete = false, bool Approve = false,
    bool Comment = false, bool ResolveApprovalComments = false, bool ReturnForCorrection = false,
    IReadOnlyList<StatusOption>? ForceTargets = null, bool Archive = false, bool Transfer = false);

/// <summary>Статус и его название для списка «Сменить статус».</summary>
public record StatusOption(string Status, string Title);

/// <summary>
/// Панель «Согласование» документа (этап 6, B2–B3): места электронного согласования и что может зритель —
/// назначить или заменить подписанта (FR-REV-13), запустить согласование принятого документа.
/// </summary>
/// <param name="Returned">Прежний круг закончился возвратом: кто, когда, с каким комментарием — методисту и администратору.</param>
/// <param name="Corrected">Согласованный документ вернули на исправление: кто, когда, причина — методисту и администратору (этап 7, A8).</param>
public record ApprovalPanel(
    IReadOnlyList<ApprovalSeatItem> Seats, bool CanAssign, bool CanStart, ApprovalReturn? Returned, ApprovalReturn? Corrected = null);

/// <summary>Место согласования: назначенный, его отметка и время, комментарий возврата.</summary>
/// <param name="CanSign">У назначенного есть действующая учётная запись с ролью «Утверждающий».</param>
/// <param name="Mine">Назначен текущий пользователь.</param>
public record ApprovalSeatItem(
    string Role, string Title, Guid? SignatoryId, string? SignatoryName, string? Position, string Status, string StatusTitle,
    DateTimeOffset? MarkedAt, string? Comment, bool CanSign, bool Mine);

/// <summary>Исправление поля из плана из журнала аудита: место, было, стало, кто и когда.</summary>
public record PlanCorrection(string Path, JsonElement? Before, JsonElement? After, string? ChangedBy, DateTimeOffset ChangedAt)
{
    public static PlanCorrection From(string payload, string? changedBy, DateTimeOffset at)
    {
        var e = JsonDocument.Parse(payload).RootElement;
        JsonElement? Value(string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Clone() : null;
        return new(e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "", Value("before"), Value("after"), changedBy, at);
    }
}

/// <summary>
/// Документ для редактора: содержимое, схема из шаблона, метаданные, доступные действия и значения по умолчанию
/// шаблона — из них редактор берёт стандартные пункты для подсказок (виды самостоятельной работы).
/// </summary>
public record DocumentDetails(
    DocumentItem Document, JsonElement Payload, JsonElement Schema, DocumentMetadata Metadata, bool Editable, DocumentActions Actions,
    JsonElement? TemplateDefaults = null, IReadOnlyList<PlanCorrection>? PlanCorrections = null, Guid? ViewerId = null,
    ApprovalPanel? Approval = null);

/// <summary>Замечание к полю: кто и когда оставил, разрешено ли и кем.</summary>
/// <param name="FromApproval">Замечание оставлено на согласовании — согласующим (этап 7, A2).</param>
public record CommentItem(
    Guid Id, string FieldPath, string Text, Guid AuthorId, string AuthorName, DateTimeOffset CreatedAt, bool IsResolved,
    string? ResolvedByName, DateTimeOffset? ResolvedAt, bool FromApproval = false)
{
    public static IQueryable<CommentItem> Query(AppDbContext db, IQueryable<DocumentComment> comments)
    {
        var fromApproval = DocumentReview.FromApproval(db);
        return from c in comments
            join a in db.Employees on c.AuthorId equals a.Id
            orderby c.CreatedAt
            select new CommentItem(c.Id, c.FieldPath, c.Text, c.AuthorId, a.ShortName, c.CreatedAt, c.IsResolved,
                db.Employees.Where(e => e.Id == c.ResolvedBy).Select(e => e.ShortName).FirstOrDefault(), c.ResolvedAt,
                fromApproval.Contains(c.Id));
    }
}
