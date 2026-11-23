using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.History;

/// <summary>
/// Срез «История документа» (этап 8, A10; FR-ADM-02): лента переходов статусов и записей аудита одного документа — методисту и
/// администратору. Автору и согласующему раздел не отдаётся: в ленте видны причины и замечания, которые не для них (403 по
/// политике). Лента — от новых к старым, частями: <c>skip</c> и <c>take</c>.
/// </summary>
public class HistoryModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app) =>
        app.MapGet("/api/documents/{id:guid}/history", Get).WithTags("History").RequireAuthorization(Policies.MethodistOrAdmin);

    private static async Task<IResult> Get(
        Guid id, AppDbContext db, DocumentTimeline timeline, CancellationToken ct, int skip = 0, int take = DocumentTimeline.DefaultPage)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Документ не найден",
                detail: "Возможно, он удалён в другой вкладке. Вернитесь к списку документов.");
        return Results.Ok(await timeline.PageAsync(document, skip, take, ct));
    }
}
