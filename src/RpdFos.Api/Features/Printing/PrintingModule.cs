using System.Security.Claims;
using Carter;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;
using RpdFos.Core.Printing;

namespace RpdFos.Api.Features.Printing;

/// <summary>
/// Срез «Печать»: формирование DOCX по шаблону оформления (этап 5, B5) и скачивание сформированных файлов.
/// Формирование пока синхронное — в фоновую очередь его переводит шаг B6. Печатает автор — в любом статусе,
/// методист — документы на проверке и принятые.
/// </summary>
public class PrintingModule : ICarterModule
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/documents/{id:guid}").WithTags("Printing").RequireAuthorization();
        g.MapPost("/print/{format}", Print);
        g.MapGet("/files/{fileId:guid}", Download);
    }

    private static async Task<IResult> Print(
        Guid id, string format, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentPrinting printing,
        CancellationToken ct)
    {
        var (document, me, refusal) = await PrintableAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (format != GenerationFormats.Docx)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Формат не поддерживается",
                detail: "Сейчас формируется только DOCX.");

        var result = await printing.GenerateDocxAsync(document!, me!.Value, ct);
        await db.SaveChangesAsync(ct);
        if (result.File is null)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Документ не сформирован",
                detail: result.Task.ErrorMessage);
        return Results.Ok(GenerationItem.From(result.Task, result.File, result.Report));
    }

    private static async Task<IResult> Download(
        Guid id, Guid fileId, HttpContext http, AppDbContext db, DirectoryAccounts directory, GeneratedFileStore store,
        TimeProvider clock, CancellationToken ct)
    {
        var (_, _, refusal) = await PrintableAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;

        var file = await (
                from f in db.GeneratedFiles
                join t in db.GenerationTasks on f.GenerationTaskId equals t.Id
                where f.Id == fileId && t.DocumentId == id
                select f)
            .SingleOrDefaultAsync(ct);
        if (file is null || file.ExpiresAt <= clock.GetUtcNow() || !store.Exists(file.StoragePath))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Файл не найден",
                detail: "Файл удалён по сроку хранения или не сформирован. Сформируйте документ заново.");
        return Results.File(store.Open(file.StoragePath), file.Format == GenerationFormats.Docx ? DocxType : "application/pdf", file.FileName);
    }

    // Автор — в любом статусе; методист — на проверке и после приёма; администратор и остальные — нет.
    private static async Task<(Document? Document, Guid? Me, IResult? Refusal)> PrintableAsync(
        Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
            return (null, null, Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Документ не найден",
                detail: "Возможно, он удалён. Вернитесь к списку документов."));
        var me = await directory.FindIdAsync(http.User, ct);
        if (me is null)
            return (null, null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
                detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору."));
        if (document.AuthorId == me || IsReviewer(http.User) && document.Status is DocumentStatuses.UnderReview or DocumentStatuses.Accepted)
            return (document, me, null);
        return (null, null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Печать недоступна",
            detail: "Печатную форму формирует автор, а методист — для документов на проверке и принятых."));
    }

    private static bool IsReviewer(ClaimsPrincipal user) => user.IsInRole(AppRoles.Methodist);
}

/// <summary>Задача формирования: статус, файл (если сформирован) и отчёт — предупреждения и незаполненные места.</summary>
public record GenerationItem(
    Guid TaskId, string Format, string Status, string? ErrorMessage, Guid? FileId, string? FileName, long? SizeBytes,
    DateTimeOffset? ExpiresAt, IReadOnlyList<string> Warnings, IReadOnlyList<string> Unfilled)
{
    public static GenerationItem From(GenerationTask task, GeneratedFile? file, PrintReport report) =>
        new(task.Id, task.Format, task.Status, task.ErrorMessage, file?.Id, file?.FileName, file?.SizeBytes, file?.ExpiresAt,
            report.Warnings, report.Unfilled);
}
