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
/// замечания, возврат и приём. Правила, предзаполнение и проверки — в ядре (<see cref="DocumentCreation"/>,
/// <see cref="DocumentEditing"/>, <see cref="DocumentReview"/>, <see cref="DocumentTransitions"/>).
/// </summary>
public class DocumentsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/documents").WithTags("Documents").RequireAuthorization();

        g.MapGet("/", List);
        g.MapGet("/disciplines", Disciplines).RequireAuthorization(Policies.DocumentAuthors);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPatch("/{id:guid}/sections/{section}", UpdateSection).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPost("/{id:guid}/save", Save).RequireAuthorization(Policies.DocumentAuthors);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.DocumentAuthors);

        g.MapPost("/{id:guid}/submit", Submit).RequireAuthorization(Policies.DocumentAuthors);
        g.MapPost("/{id:guid}/return", Return).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/{id:guid}/accept", Accept).RequireAuthorization(Policies.Reviewers);
        g.MapGet("/{id:guid}/comments", Comments);
        g.MapPost("/{id:guid}/comments", AddComment).RequireAuthorization(Policies.Reviewers);
        g.MapPost("/{id:guid}/comments/{commentId:guid}/resolve", ResolveComment).RequireAuthorization(Policies.DocumentAuthors);
    }

    // Методист и администратор видят все документы, остальные — свои.
    private static bool SeesAll(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist) || user.IsInRole(AppRoles.Admin);

    // Проверяет документы методист: возвращает, принимает, оставляет замечания, правит на проверке.
    private static bool IsReviewer(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist);

    private static async Task<List<DocumentListItem>> List(
        string? status, Guid? planId, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var query = db.Documents.AsNoTracking();
        if (!SeesAll(http.User))
        {
            var me = await directory.FindIdAsync(http.User, ct);
            query = query.Where(d => d.AuthorId == me);
        }
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

        return rows.Select(r => new DocumentListItem(r.Document.Id, r.Document.Title, r.Document.Type, r.Document.Status,
            DocumentStatuses.Title(r.Document.Status), r.Document.IsOutdated, r.Code, r.Name, plans.GetValueOrDefault(r.CurriculumPlanId),
            r.Document.TemplateRevision, r.Author, r.Document.UpdatedAt, r.Document.SubmittedAt)).ToList();
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
        var existing = await (
                from d in db.Documents
                join t in db.DocumentTemplates on d.TemplateId equals t.Id
                join a in db.Employees on d.AuthorId equals a.Id
                where t.LineageId == template.LineageId
                select new { d.DisciplineId, d.Id, d.AuthorId, a.ShortName })
            .ToDictionaryAsync(x => x.DisciplineId, ct);
        var me = await directory.FindIdAsync(http.User, ct);
        var seesAll = SeesAll(http.User);

        return Results.Ok(disciplines.Select(d => existing.TryGetValue(d.Id, out var doc)
            ? new DisciplineOption(d.Id, d.Code, d.Name, doc.Id, doc.ShortName, seesAll || doc.AuthorId == me)
            : new DisciplineOption(d.Id, d.Code, d.Name, null, null, false)).ToList());
    }

    private static async Task<IResult> Get(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, IDocumentValidator validator,
        ValidationRuleSet rules, DocumentTypeRegistry registry, CancellationToken ct)
    {
        var (document, me, refusal) = await VisibleDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        return Results.Ok(await DetailsAsync(document!, db, validator, rules, registry, ct, document!.AuthorId == me, IsReviewer(http.User)));
    }

    private static async Task<IResult> Create(
        CreateDocumentRequest rq, HttpContext http, DirectoryAccounts directory, DocumentCreation creation, AppDbContext db,
        IDocumentValidator validator, ValidationRuleSet rules, DocumentTypeRegistry registry, CancellationToken ct)
    {
        // Автор — запись справочника текущего пользователя: она же первый составитель.
        var authorId = await directory.FindIdAsync(http.User, ct);
        if (authorId is null) return NoDirectoryRecord();

        var result = await creation.CreateAsync(rq.TemplateId, rq.DisciplineId, authorId.Value, ct);
        return result switch
        {
            { Document: { } document } => Results.Created($"/api/documents/{document.Id}",
                await DetailsAsync(document, db, validator, rules, registry, ct, isAuthor: true, IsReviewer(http.User))),
            { Problem: DocumentCreationProblem.Duplicate } => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Документ уже есть", detail: result.Detail,
                extensions: new Dictionary<string, object?>
                {
                    ["documentId"] = result.ExistingId, ["location"] = $"/api/documents/{result.ExistingId}"
                }),
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
    private static async Task<IResult> UpdateSection(
        Guid id, string section, SectionUpdateRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, TimeProvider clock, CancellationToken ct)
    {
        var (document, editorId, refusal) = await EditorDocumentAsync(id, http, db, directory, DocumentTransitions.EditAction, ct);
        if (refusal is not null) return refusal;

        // Сравнивать будем с версией, которую видел клиент; новую проставит перехватчик сохранения.
        db.Entry(document!).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await editing.UpdateSectionAsync(document!, section, rq.Value, editorId, ct);
        if (result.Problem is { } problem)
            return problem switch
            {
                SectionUpdateProblem.UnknownSection => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                    title: "Раздел не найден", detail: $"Раздела «{section}» нет в структуре документа."),
                SectionUpdateProblem.HiddenSection => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Раздел не предусмотрен учебным планом", detail: "Для дисциплины этот раздел не заполняется."),
                _ => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: problem == SectionUpdateProblem.ReadOnlyChanged ? "Поля из учебного плана не изменяются" : "Раздел заполнен с ошибками",
                    detail: string.Join(" ", result.Errors.Select(e => $"{e.Path}: {e.Message}.")),
                    extensions: new Dictionary<string, object?> { ["errors"] = result.Errors })
            };

        await db.SaveChangesAsync(ct);
        return Results.Ok(new SectionSaved(document!.Version, document.Status, DocumentStatuses.Title(document.Status),
            result.Section!, clock.GetUtcNow()));
    }

    private static async Task<IResult> Save(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, TimeProvider clock, CancellationToken ct)
    {
        var (document, editorId, refusal) = await EditorDocumentAsync(id, http, db, directory, DocumentTransitions.SaveAction, ct);
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
        if (!DocumentTransitions.CanSubmit(document!.Status)) return Refused(document, DocumentTransitions.SubmitAction);

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await review.SubmitAsync(document, authorId, ct);
        if (!result.Submitted)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Документ не прошёл проверку",
                detail: "Исправьте ошибки и отправьте документ снова.",
                extensions: new Dictionary<string, object?> { ["errors"] = result.Issues });

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document.Version, document.Status, DocumentStatuses.Title(document.Status), result.Issues));
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
        if (await review.AcceptAsync(document!, reviewerId, ct) is not null)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Есть неразрешённые замечания",
                detail: "Документ с неразрешёнными замечаниями не принимается — верните его на доработку.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentReviewed(document!.Version, document.Status, DocumentStatuses.Title(document.Status), []));
    }

    /// <summary>Замечания к документу — всем, кто его видит: от старых к новым.</summary>
    private static async Task<IResult> Comments(Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var (_, _, refusal) = await VisibleDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        return Results.Ok(await CommentItem.Query(db, db.DocumentComments.Where(c => c.DocumentId == id)).ToListAsync(ct));
    }

    private static async Task<IResult> AddComment(
        Guid id, CommentRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentReview review,
        CancellationToken ct)
    {
        var (document, reviewerId, refusal) = await ReviewedDocumentAsync(id, http, db, directory, DocumentTransitions.CommentAction, ct);
        if (refusal is not null) return refusal;

        var (comment, problem) = await review.CommentAsync(document!, reviewerId, rq.FieldPath ?? "", rq.Text ?? "", ct);
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

    /// <summary>Автор отмечает замечание разрешённым, пока документ на доработке.</summary>
    private static async Task<IResult> ResolveComment(
        Guid id, Guid commentId, HttpContext http, AppDbContext db, DirectoryAccounts directory, TimeProvider clock,
        CancellationToken ct)
    {
        var (document, authorId, refusal) = await AuthorDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (!DocumentTransitions.CanResolveComments(document!.Status)) return Refused(document, DocumentTransitions.ResolveAction);

        var comment = await db.DocumentComments.SingleOrDefaultAsync(c => c.Id == commentId && c.DocumentId == id, ct);
        if (comment is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Замечание не найдено",
                detail: "Обновите страницу документа.");
        if (!comment.Resolve(authorId, clock.GetUtcNow()))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Замечание уже разрешено",
                detail: "Его отметили раньше — возможно, в другой вкладке.");

        await db.SaveChangesAsync(ct);
        return Results.Ok(await CommentItem.Query(db, db.DocumentComments.Where(c => c.Id == commentId)).SingleAsync(ct));
    }

    // Документ видят автор, методист и администратор.
    private static async Task<(Document? Document, Guid? Me, IResult? Refusal)> VisibleDocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, null, NotFound());
        var me = await directory.FindIdAsync(http.User, ct);
        if (!SeesAll(http.User) && document.AuthorId != me)
            return (null, null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужой документ",
                detail: "Документ видят его автор, методист и администратор."));
        return (document, me, null);
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

    // Возвращает, принимает и оставляет замечания методист — пока документ на проверке.
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
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, string action, CancellationToken ct)
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
        if (isAuthor || isReviewer) return (null, default, Refused(document, action));
        return (null, default, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Изменяет только автор",
            detail: "Документ изменяет его автор, а на проверке — методист."));
    }

    private static async Task<DocumentDetails> DetailsAsync(
        Document document, AppDbContext db, IDocumentValidator validator, ValidationRuleSet rules, DocumentTypeRegistry registry,
        CancellationToken ct,
        bool isAuthor, bool isReviewer)
    {
        var schemaJson = await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId)
            .Select(t => t.Schema).SingleAsync(ct);
        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        var payload = JsonDocument.Parse(document.Payload).RootElement;
        var status = document.Status;

        return new DocumentDetails(
            DocumentItem.From(document, facts.Discipline),
            payload,
            JsonDocument.Parse(schemaJson).RootElement,
            DocumentMetadata.Build(schemaJson, payload, status, facts, validator, rules, registry.FindSchema),
            // Остальным редактор показывает документ только для чтения.
            isAuthor && DocumentTransitions.CanEdit(status, DocumentRole.Author) ||
            isReviewer && DocumentTransitions.CanEdit(status, DocumentRole.Reviewer),
            new DocumentActions(
                Submit: isAuthor && DocumentTransitions.CanSubmit(status),
                Review: isReviewer && DocumentTransitions.CanReview(status),
                ResolveComments: isAuthor && DocumentTransitions.CanResolveComments(status),
                // Печать: автор — в любом статусе, методист — на проверке и после приёма (как в срезе «Печать»).
                Print: isAuthor || isReviewer && status is DocumentStatuses.UnderReview or DocumentStatuses.Accepted));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Документ не найден",
            detail: "Возможно, он удалён в другой вкладке. Вернитесь к списку документов.");

    private static IResult Refused(Document document, string action) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Действие недоступно в этом статусе",
            detail: DocumentTransitions.Refusal(document.Status, action));

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");
}

public record CreateDocumentRequest(Guid TemplateId, Guid DisciplineId);

/// <summary>Значение раздела целиком и версия документа, которую видел клиент.</summary>
public record SectionUpdateRequest(int Version, JsonElement Value);

public record DocumentVersionRequest(int Version);

/// <summary>Возврат на доработку: версия и необязательный общий комментарий — замечания к полям уже есть.</summary>
public record ReturnRequest(int Version, string? Comment);

/// <summary>Замечание: JSON Pointer поля и текст.</summary>
public record CommentRequest(string? FieldPath, string? Text);

/// <summary>Раздел сохранён: новая версия, статус (после «Сохранить» правка возвращает черновик) и заполненность раздела.</summary>
public record SectionSaved(int Version, string Status, string StatusTitle, SectionState Section, DateTimeOffset SavedAt);

public record DocumentSaved(int Version, string Status, string StatusTitle, DateTimeOffset SavedAt);

/// <summary>Статус сменился: новая версия и статус; при отправке — предупреждения полной проверки.</summary>
public record DocumentReviewed(int Version, string Status, string StatusTitle, IReadOnlyList<DocumentValidationError> Warnings);

public record DocumentListItem(
    Guid Id, string Title, string Type, string Status, string StatusTitle, bool IsOutdated, string DisciplineCode,
    string DisciplineName, PlanItem? Plan, int TemplateRevision, string AuthorName, DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt);

/// <summary>Дисциплина плана для создания документа; есть документ — ссылка на него, если он доступен пользователю.</summary>
public record DisciplineOption(Guid Id, string Code, string Name, Guid? DocumentId, string? DocumentAuthor, bool CanOpen);

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
/// замечания, формировать печатную форму.
/// </summary>
public record DocumentActions(bool Submit, bool Review, bool ResolveComments, bool Print);

/// <summary>Документ для редактора: содержимое, схема из шаблона, метаданные и доступные действия.</summary>
public record DocumentDetails(
    DocumentItem Document, JsonElement Payload, JsonElement Schema, DocumentMetadata Metadata, bool Editable, DocumentActions Actions);

/// <summary>Замечание к полю: кто и когда оставил, разрешено ли и кем.</summary>
public record CommentItem(
    Guid Id, string FieldPath, string Text, Guid AuthorId, string AuthorName, DateTimeOffset CreatedAt, bool IsResolved,
    string? ResolvedByName, DateTimeOffset? ResolvedAt)
{
    public static IQueryable<CommentItem> Query(AppDbContext db, IQueryable<DocumentComment> comments) =>
        from c in comments
        join a in db.Employees on c.AuthorId equals a.Id
        orderby c.CreatedAt
        select new CommentItem(c.Id, c.FieldPath, c.Text, c.AuthorId, a.ShortName, c.CreatedAt, c.IsResolved,
            db.Employees.Where(e => e.Id == c.ResolvedBy).Select(e => e.ShortName).FirstOrDefault(), c.ResolvedAt);
}
