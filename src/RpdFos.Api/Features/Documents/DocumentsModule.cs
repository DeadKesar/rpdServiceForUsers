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
/// (преподаватель видит свои, FR-DOC-08), изменение по разделам, сохранение, удаление. Правила, предзаполнение
/// и проверки — в ядре (<see cref="DocumentCreation"/>, <see cref="DocumentEditing"/>, <see cref="DocumentTransitions"/>).
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
    }

    // Методист и администратор видят все документы, остальные — свои.
    private static bool SeesAll(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist) || user.IsInRole(AppRoles.Admin);

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
            r.Document.TemplateRevision, r.Author, r.Document.UpdatedAt)).ToList();
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
        DocumentTypeRegistry registry, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return NotFound();
        if (!SeesAll(http.User) && document.AuthorId != await directory.FindIdAsync(http.User, ct))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Чужой документ",
                detail: "Документ видят его автор, методист и администратор.");
        return Results.Ok(await DetailsAsync(document, db, validator, registry, ct));
    }

    private static async Task<IResult> Create(
        CreateDocumentRequest rq, HttpContext http, DirectoryAccounts directory, DocumentCreation creation, AppDbContext db,
        IDocumentValidator validator, DocumentTypeRegistry registry, CancellationToken ct)
    {
        // Автор — запись справочника текущего пользователя: она же первый составитель.
        var authorId = await directory.FindIdAsync(http.User, ct);
        if (authorId is null) return NoDirectoryRecord();

        var result = await creation.CreateAsync(rq.TemplateId, rq.DisciplineId, authorId.Value, ct);
        return result switch
        {
            { Document: { } document } =>
                Results.Created($"/api/documents/{document.Id}", await DetailsAsync(document, db, validator, registry, ct)),
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
    /// синхронизация нормализованных таблиц. Ответ — новая версия и заполненность раздела.
    /// </summary>
    private static async Task<IResult> UpdateSection(
        Guid id, string section, SectionUpdateRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, TimeProvider clock, CancellationToken ct)
    {
        var (document, editorId, refusal) = await AuthorDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (!DocumentTransitions.CanEdit(document!.Status)) return Refused(document, DocumentTransitions.EditAction);

        // Сравнивать будем с версией, которую видел клиент; новую проставит перехватчик сохранения.
        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        var result = await editing.UpdateSectionAsync(document, section, rq.Value, editorId, ct);
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
        return Results.Ok(new SectionSaved(document.Version, document.Status, DocumentStatuses.Title(document.Status),
            result.Section!, clock.GetUtcNow()));
    }

    private static async Task<IResult> Save(
        Guid id, DocumentVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        DocumentEditing editing, TimeProvider clock, CancellationToken ct)
    {
        var (document, editorId, refusal) = await AuthorDocumentAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (!DocumentTransitions.CanSave(document!.Status)) return Refused(document, DocumentTransitions.SaveAction);

        db.Entry(document).Property(d => d.Version).OriginalValue = rq.Version;
        await editing.SaveAsync(document, editorId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new DocumentSaved(document.Version, document.Status, DocumentStatuses.Title(document.Status), clock.GetUtcNow()));
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

    // Изменяет документ только его автор.
    private static async Task<(Document? Document, Guid EditorId, IResult? Refusal)> AuthorDocumentAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null) return (null, default, NotFound());
        var me = await directory.FindIdAsync(http.User, ct);
        if (me is null) return (null, default, NoDirectoryRecord());
        if (document.AuthorId != me)
            return (null, default, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Изменяет только автор",
                detail: "Документ изменяет и удаляет его автор."));
        return (document, me.Value, null);
    }

    private static async Task<DocumentDetails> DetailsAsync(
        Document document, AppDbContext db, IDocumentValidator validator, DocumentTypeRegistry registry, CancellationToken ct)
    {
        var schemaJson = await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId)
            .Select(t => t.Schema).SingleAsync(ct);
        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        var payload = JsonDocument.Parse(document.Payload).RootElement;

        return new DocumentDetails(
            DocumentItem.From(document, facts.Discipline),
            payload,
            JsonDocument.Parse(schemaJson).RootElement,
            DocumentMetadata.Build(schemaJson, payload, document.Status, facts, validator, registry.FindSchema));
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

/// <summary>Раздел сохранён: новая версия, статус (после «Сохранить» правка возвращает черновик) и заполненность раздела.</summary>
public record SectionSaved(int Version, string Status, string StatusTitle, SectionState Section, DateTimeOffset SavedAt);

public record DocumentSaved(int Version, string Status, string StatusTitle, DateTimeOffset SavedAt);

public record DocumentListItem(
    Guid Id, string Title, string Type, string Status, string StatusTitle, bool IsOutdated, string DisciplineCode,
    string DisciplineName, PlanItem? Plan, int TemplateRevision, string AuthorName, DateTimeOffset UpdatedAt);

/// <summary>Дисциплина плана для создания документа; есть документ — ссылка на него, если он доступен пользователю.</summary>
public record DisciplineOption(Guid Id, string Code, string Name, Guid? DocumentId, string? DocumentAuthor, bool CanOpen);

public record DocumentItem(
    Guid Id, string Type, string Title, string Status, string StatusTitle, bool IsOutdated, int Version, Guid TemplateId,
    int TemplateRevision, Guid DisciplineId, string DisciplineCode, string DisciplineName, Guid AuthorId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static DocumentItem From(Document d, Discipline discipline) =>
        new(d.Id, d.Type, d.Title, d.Status, DocumentStatuses.Title(d.Status), d.IsOutdated, d.Version, d.TemplateId,
            d.TemplateRevision, d.DisciplineId, discipline.Code, discipline.Name, d.AuthorId, d.CreatedAt, d.UpdatedAt);
}

/// <summary>Документ для редактора: содержимое, схема из шаблона и метаданные (разделы, доступность полей, кандидаты).</summary>
public record DocumentDetails(DocumentItem Document, JsonElement Payload, JsonElement Schema, DocumentMetadata Metadata);
