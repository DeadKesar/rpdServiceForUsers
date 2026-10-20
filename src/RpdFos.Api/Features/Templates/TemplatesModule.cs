using System.Security.Cryptography;
using Carter;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Plans;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;

namespace RpdFos.Api.Features.Templates;

/// <summary>
/// Срез «Шаблоны»: шаблон для версии учебного плана, DOCX-шаблон оформления и сопоставление закладок,
/// жизненный цикл из шести состояний и редакции. Правила переходов и проверка — в ядре
/// (<see cref="TemplateTransitions"/>, <see cref="TemplateCheck"/>, <see cref="TemplateLifecycle"/>).
/// </summary>
public class TemplatesModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Читать шаблоны может любой вошедший пользователь, изменять — методист и администратор.
        var g = app.MapGroup("/api/templates").WithTags("Templates").RequireAuthorization();

        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapPost("/{id:guid}/print-template", UploadPrintTemplate)
            .Accepts<IFormFile>("multipart/form-data")
            .RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapPost("/{id:guid}/save", Save).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapPost("/{id:guid}/publish", Publish).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapPost("/{id:guid}/archive", Archive).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapPost("/{id:guid}/revisions", CreateRevision).RequireAuthorization(Policies.MethodistOrAdmin);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.MethodistOrAdmin);
    }

    private static async Task<List<TemplateItem>> List(
        string? type, string? status, Guid? planId, AppDbContext db, DocumentTypeRegistry registry, CancellationToken ct)
    {
        var query = db.DocumentTemplates.AsNoTracking();
        if (!string.IsNullOrEmpty(type)) query = query.Where(t => t.Type == type);
        if (!string.IsNullOrEmpty(status)) query = query.Where(t => t.Status == status);
        if (planId is not null) query = query.Where(t => t.CurriculumPlanId == planId);
        var templates = await query.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

        var planIds = templates.Select(t => t.CurriculumPlanId).OfType<Guid>().Distinct().ToList();
        var plans = await PlanItem.Query(db).Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return templates.Select(t => TemplateItem.From(t, registry,
            t.CurriculumPlanId is Guid p ? plans.GetValueOrDefault(p) : null)).ToList();
    }

    private static async Task<IResult> Get(
        Guid id, AppDbContext db, TemplateLifecycle lifecycle, DocumentTypeRegistry registry, CancellationToken ct)
    {
        var t = await db.DocumentTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? TemplateNotFound() : Results.Ok(await DetailsAsync(t, db, lifecycle, registry, ct));
    }

    private static async Task<IResult> Create(
        CreateTemplateRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        TemplateLifecycle lifecycle, DocumentTypeRegistry registry, CancellationToken ct)
    {
        if (!registry.TryGet(rq.Type, out var type))
            return Invalid("Неизвестный тип документа", $"Тип «{rq.Type}» не поддерживается.");
        var name = rq.Name?.Trim() ?? "";
        if (name.Length is 0 or > 255)
            return Invalid("Неверное название", "Название шаблона — от 1 до 255 символов.");
        if (!await db.CurriculumPlans.AnyAsync(p => p.Id == rq.CurriculumPlanId, ct))
            return Invalid("Учебный план не найден", "Шаблон создаётся для импортированной версии учебного плана.");

        var createdBy = await directory.FindIdAsync(http.User, ct);
        if (createdBy is null) return NoDirectoryRecord();

        var t = DocumentTemplate.Create(type, name, rq.CurriculumPlanId, createdBy.Value);
        db.DocumentTemplates.Add(t);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/templates/{t.Id}", await DetailsAsync(t, db, lifecycle, registry, ct));
    }

    /// <summary>
    /// DOCX-шаблон оформления: файл — в uploaded_files, закладки читаются из него, сопоставление по умолчанию —
    /// из реестра типов. Поля формы: <c>file</c> (.docx) и <c>version</c> — версия шаблона у клиента.
    /// </summary>
    private static async Task<IResult> UploadPrintTemplate(
        Guid id, HttpRequest request, AppDbContext db, DirectoryAccounts directory, TemplateLifecycle lifecycle,
        DocumentTypeRegistry registry, IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var t = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return TemplateNotFound();
        if (!TemplateTransitions.CanAttachPrintTemplate(t.Status))
            return Refused(t, TemplateTransitions.AttachAction);

        var uploadedBy = await directory.FindIdAsync(request.HttpContext.User, ct);
        if (uploadedBy is null) return NoDirectoryRecord();

        if (!request.HasFormContentType)
            return Invalid("Ожидается multipart/form-data", "Поля формы: file — DOCX-шаблон оформления, version — версия шаблона.");
        var form = await request.ReadFormAsync(ct);
        if (!int.TryParse(form["version"], out var version))
            return Invalid("Не указана версия шаблона", "Поле version — версия шаблона, которую видел клиент.");
        var file = form.Files.GetFile("file");
        var maxBytes = config.GetValue("Templates:MaxFileBytes", 10L * 1024 * 1024);
        if (file is null)
            return Invalid("Файл не выбран", "Нужен DOCX-шаблон оформления с закладками Autofill.");
        if (!string.Equals(Path.GetExtension(file.FileName), ".docx", StringComparison.OrdinalIgnoreCase))
            return Invalid("Нужен файл .docx", $"Загружен «{file.FileName}».");
        if (file.Length == 0 || file.Length > maxBytes)
            return Invalid("Недопустимый размер файла", $"Размер {file.Length} байт, допустимо от 1 до {maxBytes}.");

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        var bookmarks = DocxBookmarks.TryRead(content);
        if (bookmarks is null)
            return Invalid("Файл не читается как документ Word", $"«{file.FileName}» не открывается как DOCX.");

        // Тот же файл уже загружался — используется сохранённый, как при импорте.
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
        var fileId = await db.UploadedFiles
            .Where(f => f.Checksum == checksum && f.FileType == UploadedFileTypes.Other)
            .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
        if (fileId is null)
        {
            var uploaded = new UploadedFile
            {
                UploadedBy = uploadedBy.Value, FileType = UploadedFileTypes.Other, OriginalName = Path.GetFileName(file.FileName),
                Content = content, MimeType = file.ContentType, SizeBytes = content.Length, Checksum = checksum,
                UploadedAt = clock.GetUtcNow()
            };
            db.UploadedFiles.Add(uploaded);
            fileId = uploaded.Id;
        }

        t.AttachPrintTemplate(fileId.Value, TemplateCheck.DefaultMapping(registry.Get(t.Type), bookmarks));
        db.Entry(t).Property(x => x.Version).OriginalValue = version;
        await db.SaveChangesAsync(ct);

        return Results.Ok(await DetailsAsync(t, db, lifecycle, registry, ct));
    }

    // Образец среза «смена статуса + проверка версии»: загрузить -> 404 -> правило перехода (409)
    // -> изменить -> сохранить. Конфликт версий превращает в 409 ConcurrencyExceptionHandler.
    private static async Task<IResult> Save(
        Guid id, TemplateVersionRequest rq, AppDbContext db, TemplateLifecycle lifecycle, DocumentTypeRegistry registry,
        CancellationToken ct)
    {
        var t = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return TemplateNotFound();
        if (!TemplateTransitions.CanSave(t.Status)) return Refused(t, TemplateTransitions.SaveAction);

        t.Save();
        // Сравнивать будем с версией, которую видел клиент.
        // Новую версию и время изменения проставит перехватчик сохранения.
        db.Entry(t).Property(x => x.Version).OriginalValue = rq.Version;
        await db.SaveChangesAsync(ct);

        return Results.Ok(await DetailsAsync(t, db, lifecycle, registry, ct));
    }

    private static async Task<IResult> Publish(
        Guid id, TemplateVersionRequest rq, AppDbContext db, TemplateLifecycle lifecycle, DocumentTypeRegistry registry,
        CancellationToken ct)
    {
        var t = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return TemplateNotFound();
        if (!TemplateTransitions.CanPublish(t.Status)) return Refused(t, TemplateTransitions.PublishAction);

        db.Entry(t).Property(x => x.Version).OriginalValue = rq.Version;

        // Проверка перед публикацией (FR-TPL-11, FR-TPL-13): ошибки — списком. Прежнюю редакцию архивирует ядро.
        var errors = await lifecycle.PublishAsync(t, ct);
        if (errors.Count > 0)
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Шаблон не прошёл проверку перед публикацией",
                detail: string.Join(" ", errors),
                extensions: new Dictionary<string, object?> { ["errors"] = errors });

        return Results.Ok(await DetailsAsync(t, db, lifecycle, registry, ct));
    }

    private static async Task<IResult> Archive(
        Guid id, TemplateVersionRequest rq, AppDbContext db, TemplateLifecycle lifecycle, DocumentTypeRegistry registry,
        CancellationToken ct)
    {
        var t = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return TemplateNotFound();
        if (!TemplateTransitions.CanArchive(t.Status)) return Refused(t, TemplateTransitions.ArchiveAction);

        t.Archive();
        db.Entry(t).Property(x => x.Version).OriginalValue = rq.Version;
        await db.SaveChangesAsync(ct);

        return Results.Ok(await DetailsAsync(t, db, lifecycle, registry, ct));
    }

    /// <summary>Новая редакция опубликованного шаблона — новая запись в статусе «Черновик».</summary>
    private static async Task<IResult> CreateRevision(
        Guid id, TemplateVersionRequest rq, HttpContext http, AppDbContext db, DirectoryAccounts directory,
        TemplateLifecycle lifecycle, DocumentTypeRegistry registry, CancellationToken ct)
    {
        var t = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return TemplateNotFound();
        if (!TemplateTransitions.CanCreateRevision(t.Status)) return Refused(t, TemplateTransitions.RevisionAction);
        // Опубликованная редакция при этом не меняется, поэтому версия клиента сравнивается явно.
        if (rq.Version != t.Version) return Results.Problem(ConcurrencyExceptionHandler.VersionConflict());
        if (await lifecycle.HasRevisionInWorkAsync(t.LineageId, ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Новая редакция уже в работе",
                detail: "У шаблона есть неопубликованная редакция — продолжите её или удалите.");

        var createdBy = await directory.FindIdAsync(http.User, ct);
        if (createdBy is null) return NoDirectoryRecord();

        var revision = await lifecycle.CreateRevisionAsync(t, createdBy.Value, ct);
        return Results.Created($"/api/templates/{revision.Id}", await DetailsAsync(revision, db, lifecycle, registry, ct));
    }

    private static async Task<IResult> Delete(Guid id, int version, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var t = await db.DocumentTemplates.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return TemplateNotFound();
        if (!TemplateTransitions.CanDelete(t.Status)) return Refused(t, TemplateTransitions.DeleteAction);

        t.Delete(clock.GetUtcNow());
        db.Entry(t).Property(x => x.Version).OriginalValue = version;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<TemplateDetails> DetailsAsync(
        DocumentTemplate t, AppDbContext db, TemplateLifecycle lifecycle, DocumentTypeRegistry registry, CancellationToken ct)
    {
        var plan = t.CurriculumPlanId is Guid planId
            ? await PlanItem.Query(db).SingleOrDefaultAsync(p => p.Id == planId, ct)
            : null;
        var printTemplateName = t.PrintTemplateFileId is Guid fileId
            ? await db.UploadedFiles.Where(f => f.Id == fileId).Select(f => f.OriginalName).SingleAsync(ct)
            : null;
        var inspection = await lifecycle.InspectAsync(t, ct);

        return new TemplateDetails(
            TemplateItem.From(t, registry, plan),
            printTemplateName,
            inspection.Bookmarks.Select(b => new BookmarkRow(b.BaseName, b.Count, b.Names,
                inspection.Mapping.GetValueOrDefault(b.BaseName))).ToList(),
            inspection.Signatures.Select(s => new SignatureRow(s.Id, s.Title, s.Role, SignatoryRoles.Title(s.Role),
                s.Resolver, SignatureResolvers.All.GetValueOrDefault(s.Resolver), s.Electronic, s.Placement)).ToList(),
            inspection.Errors);
    }

    private static IResult Invalid(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: title, detail: detail);

    private static IResult TemplateNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Шаблон не найден",
            detail: "Возможно, он удалён в другой вкладке. Вернитесь к списку шаблонов.");

    private static IResult Refused(DocumentTemplate t, string action) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Переход статуса невозможен",
            detail: TemplateTransitions.Refusal(t.Status, action));

    private static IResult NoDirectoryRecord() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
            detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");
}

public record CreateTemplateRequest(string Type, string Name, Guid CurriculumPlanId);

/// <summary>Версия шаблона, которую видел клиент, — для оптимистичной блокировки.</summary>
public record TemplateVersionRequest(int Version);

public record TemplateItem(
    Guid Id, string Type, string TypeTitle, string Name, string Status, string StatusTitle, int Version, int Revision,
    Guid LineageId, PlanItem? Plan, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt)
{
    public static TemplateItem From(DocumentTemplate t, DocumentTypeRegistry registry, PlanItem? plan) =>
        new(t.Id, t.Type, registry.TryGet(t.Type, out var type) ? type.Title : t.Type, t.Name, t.Status,
            TemplateStatuses.Title(t.Status), t.Version, t.Revision, t.LineageId, plan, t.CreatedAt, t.PublishedAt);
}

/// <summary>Шаблон с закладками оформления, листом согласования и результатом проверки перед публикацией.</summary>
public record TemplateDetails(
    TemplateItem Template, string? PrintTemplateName, IReadOnlyList<BookmarkRow> Bookmarks,
    IReadOnlyList<SignatureRow> Signatures, IReadOnlyList<string> Errors)
{
    public bool CanPublish => Errors.Count == 0;
}

/// <summary>Закладка шаблона оформления: вхождения и цель; цели нет — закладка не сопоставлена.</summary>
public record BookmarkRow(string BaseName, int Count, IReadOnlyList<string> Names, BookmarkTarget? Target);

/// <summary>Место листа согласования: коды категории и правила — с русскими названиями для интерфейса.</summary>
public record SignatureRow(
    string Id, string Title, string Role, string RoleTitle, string Resolver, string? ResolverTitle, bool Electronic,
    string Placement);
