using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Api.Features.Templates;

/// <summary>Срез «Шаблоны»: список, создание, публикация (контракт API, раздел «Шаблоны»).</summary>
public class TemplatesModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/templates").WithTags("Templates");

        g.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            await db.DocumentTemplates
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => new TemplateListItem(t.Id, t.Type, t.Name, t.Status, t.Version, t.Year))
                .ToListAsync(ct));

        g.MapPost("/", async (CreateTemplateRequest rq, AppDbContext db, CancellationToken ct) =>
        {
            if (rq.Type is not (TemplateTypes.Rpd or TemplateTypes.Fos))
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Неизвестный тип документа",
                    detail: $"Тип «{rq.Type}» не поддерживается.");

            var t = new DocumentTemplate { Type = rq.Type, Name = rq.Name, Year = rq.Year };
            db.Add(t);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/templates/{t.Id}", TemplateListItem.From(t));
        });

        // Образец среза «смена статуса + проверка версии»: загрузить -> 404 -> правило перехода (409)
        // -> изменить -> сохранить. Конфликт версий превращает в 409 ConcurrencyExceptionHandler.
        g.MapPost("/{id:guid}/publish", async (
            Guid id, PublishTemplateRequest rq, AppDbContext db, CancellationToken ct) =>
        {
            var t = await db.DocumentTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null)
                return Results.NotFound();

            if (!TemplateTransitions.CanPublish(t.Status))
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Переход статуса невозможен",
                    detail: $"Шаблон в статусе «{t.Status}» нельзя опубликовать.");

            t.Status = TemplateStatuses.Published;

            // Сравнивать будем с версией, которую видел клиент.
            // Новую версию и время изменения проставит перехватчик сохранения.
            db.Entry(t).Property(x => x.Version).OriginalValue = rq.Version;
            await db.SaveChangesAsync(ct);

            return Results.Ok(TemplateListItem.From(t));
        });
    }
}

public record CreateTemplateRequest(string Type, string Name, int Year);

public record PublishTemplateRequest(int Version);

public record TemplateListItem(Guid Id, string Type, string Name, string Status, int Version, int Year)
{
    public static TemplateListItem From(DocumentTemplate t) =>
        new(t.Id, t.Type, t.Name, t.Status, t.Version, t.Year);
}
