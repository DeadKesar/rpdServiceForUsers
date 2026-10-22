using System.Security.Claims;
using Carter;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Documents;
using RpdFos.Core.Printing;

namespace RpdFos.Api.Features.Printing;

/// <summary>
/// Срез «Печать»: формирование DOCX и PDF по шаблону оформления — фоновой задачей в очереди generation (FR-GEN-04),
/// статус задачи для опроса из клиента, скачивание сформированных файлов. Печатает автор — в любом статусе,
/// методист — документы на проверке и принятые.
/// </summary>
public class PrintingModule : ICarterModule
{
    private const int HistorySize = 10;

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/documents/{id:guid}").WithTags("Printing").RequireAuthorization();
        g.MapPost("/print/{format}", Print);
        g.MapGet("/generations", Generations);
        g.MapGet("/generations/{taskId:guid}", Generation);
        g.MapGet("/files/{fileId:guid}", Download);
    }

    private static async Task<IResult> Print(
        Guid id, string format, HttpContext http, AppDbContext db, DirectoryAccounts directory, DocumentPrinting printing,
        IBackgroundJobClient jobs, CancellationToken ct)
    {
        var (document, me, refusal) = await PrintableAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        if (!GenerationFormats.All.Contains(format))
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Формат не поддерживается",
                detail: "Документ формируется в DOCX или PDF.");

        var task = printing.Queue(document!, format, me!.Value);
        await db.SaveChangesAsync(ct);
        task.HangfireJobId = jobs.Enqueue<GenerationJob>(JobQueues.Generation, job => job.RunAsync(task.Id, CancellationToken.None));
        await db.SaveChangesAsync(ct);
        return Results.Accepted($"/api/documents/{id}/generations/{task.Id}", GenerationItem.From(task, null));
    }

    /// <summary>Последние задачи формирования документа — с файлами и отчётами.</summary>
    private static async Task<IResult> Generations(Guid id, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var (_, _, refusal) = await PrintableAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        var tasks = await db.GenerationTasks.AsNoTracking().Where(t => t.DocumentId == id)
            .OrderByDescending(t => t.CreatedAt).Take(HistorySize).ToListAsync(ct);
        return Results.Ok(await ItemsAsync(db, tasks, ct));
    }

    private static async Task<IResult> Generation(
        Guid id, Guid taskId, HttpContext http, AppDbContext db, DirectoryAccounts directory, CancellationToken ct)
    {
        var (_, _, refusal) = await PrintableAsync(id, http, db, directory, ct);
        if (refusal is not null) return refusal;
        var task = await db.GenerationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.DocumentId == id, ct);
        if (task is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Задача формирования не найдена",
                detail: "Сформируйте документ заново.");
        return Results.Ok((await ItemsAsync(db, [task], ct)).Single());
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
        return Results.File(store.Open(file.StoragePath), ContentType(file.Format), file.FileName);
    }

    private static string ContentType(string format) => format == GenerationFormats.Pdf
        ? "application/pdf"
        : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static async Task<List<GenerationItem>> ItemsAsync(AppDbContext db, List<GenerationTask> tasks, CancellationToken ct)
    {
        var ids = tasks.Select(t => t.Id).ToList();
        var files = await db.GeneratedFiles.AsNoTracking().Where(f => ids.Contains(f.GenerationTaskId))
            .ToDictionaryAsync(f => f.GenerationTaskId, ct);
        return tasks.Select(t => GenerationItem.From(t, files.GetValueOrDefault(t.Id))).ToList();
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

/// <summary>
/// Задача формирования: формат, статус (в очереди, выполняется, готово, ошибка), файл, если сформирован, и отчёт —
/// предупреждения и незаполненные места.
/// </summary>
public record GenerationItem(
    Guid TaskId, string Format, string Status, string? ErrorMessage, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    Guid? FileId, string? FileName, long? SizeBytes, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Unfilled)
{
    public static GenerationItem From(GenerationTask task, GeneratedFile? file)
    {
        var report = DocumentPrinting.ReadReport(task.Report);
        return new(task.Id, task.Format, task.Status, task.ErrorMessage, task.CreatedAt, task.CompletedAt, file?.Id, file?.FileName,
            file?.SizeBytes, file?.ExpiresAt, report.Warnings, report.Unfilled);
    }
}
