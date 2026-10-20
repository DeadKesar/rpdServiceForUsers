using System.Text.Json;
using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Api.Features.Documents;

/// <summary>
/// Срез «Документы»: создание РПД по опубликованному шаблону для дисциплины плана с предзаполнением из плана
/// и справочников. Правила и предзаполнение — в ядре (<see cref="DocumentCreation"/>, <see cref="DocumentPrefill"/>).
/// </summary>
public class DocumentsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/documents").WithTags("Documents").RequireAuthorization();

        g.MapPost("/", Create).RequireAuthorization(Policies.DocumentAuthors);
    }

    private static async Task<IResult> Create(
        CreateDocumentRequest rq, HttpContext http, DirectoryAccounts directory, DocumentCreation creation, AppDbContext db,
        IDocumentValidator validator, DocumentTypeRegistry registry, CancellationToken ct)
    {
        // Автор — запись справочника текущего пользователя: она же первый составитель.
        var authorId = await directory.FindIdAsync(http.User, ct);
        if (authorId is null)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
                detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");

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
}

public record CreateDocumentRequest(Guid TemplateId, Guid DisciplineId);

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
