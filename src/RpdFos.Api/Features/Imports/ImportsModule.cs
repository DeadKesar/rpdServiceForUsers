using System.Security.Cryptography;
using Carter;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Infrastructure;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Import;

namespace RpdFos.Api.Features.Imports;

/// <summary>
/// Срез «Импорт данных»: методист загружает исходные файлы, разбор и импорт идут в фоне (очередь parsing),
/// итог — в parser_runs. Повторная загрузка тех же файлов разрешена: она создаёт новую версию плана.
/// </summary>
public class ImportsModule : ICarterModule
{
    /// <summary>Поля формы: имя, тип файла в uploaded_files, расширение, обязательность.</summary>
    private static readonly (string Field, string Type, string Extension, bool Required, string Title)[] Slots =
    [
        ("plan", UploadedFileTypes.CurriculumPlan, ".xlsx", true, "Учебный план"),
        ("competences", UploadedFileTypes.CompetenceReference, ".docx", true, "Список компетенций"),
        ("matrix", UploadedFileTypes.CompetenceMatrix, ".docx", true, "Матрица компетенций"),
        ("positions", UploadedFileTypes.Employees, ".xlsx", false, "Должности сотрудников")
    ];

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/imports").WithTags("Imports").RequireAuthorization(Policies.MethodistOrAdmin);

        g.MapPost("/", Upload)
            .Accepts<IFormFileCollection>("multipart/form-data")
            .Produces<ImportAccepted>(StatusCodes.Status202Accepted);
        g.MapGet("/{id:guid}", Get);
        g.MapGet("/", List);
    }

    private static async Task<IResult> Upload(
        HttpRequest request, AppDbContext db, DirectoryAccounts directory, IBackgroundJobClient jobs,
        IConfiguration config, TimeProvider clock, CancellationToken ct)
    {
        var startedBy = await directory.FindIdAsync(request.HttpContext.User, ct);
        if (startedBy is null)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Нет записи в справочнике сотрудников",
                detail: "Учётная запись не связана со справочником сотрудников — обратитесь к администратору.");

        if (!request.HasFormContentType)
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Ожидается multipart/form-data");
        var form = await request.ReadFormAsync(ct);
        var maxBytes = config.GetValue("Imports:MaxFileBytes", 10L * 1024 * 1024);

        // Проверка: обязательные файлы, расширение, размер, один и тот же файл в двух полях.
        var errors = new Dictionary<string, string[]>();
        var files = new List<(string Field, string Type, IFormFile File, byte[] Content, string Checksum)>();
        foreach (var slot in Slots)
        {
            var file = form.Files.GetFile(slot.Field);
            if (file is null)
            {
                if (slot.Required) errors[slot.Field] = [$"{slot.Title}: файл не выбран."];
                continue;
            }
            if (!string.Equals(Path.GetExtension(file.FileName), slot.Extension, StringComparison.OrdinalIgnoreCase))
                errors[slot.Field] = [$"{slot.Title}: нужен файл {slot.Extension}, загружен «{file.FileName}»."];
            else if (file.Length == 0 || file.Length > maxBytes)
                errors[slot.Field] = [$"{slot.Title}: размер {file.Length} байт, допустимо от 1 до {maxBytes}."];
            else
            {
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                var content = buffer.ToArray();
                files.Add((slot.Field, slot.Type, file, content, Convert.ToHexStringLower(SHA256.HashData(content))));
            }
        }
        foreach (var duplicate in files.GroupBy(f => f.Checksum).Where(g => g.Count() > 1))
            foreach (var f in duplicate)
                errors[f.Field] = [$"Один и тот же файл загружен в поля {string.Join(", ", duplicate.Select(x => x.Field))}."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Файлы не приняты");

        // Тот же файл уже загружался — используется сохранённый: содержимое в базе не дублируется.
        var checksums = files.Select(f => f.Checksum).ToList();
        var known = await db.UploadedFiles.Where(f => checksums.Contains(f.Checksum))
            .Select(f => new { f.Id, f.Checksum, f.FileType }).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var ids = new Dictionary<string, Guid>();
        foreach (var f in files)
        {
            if (known.FirstOrDefault(k => k.Checksum == f.Checksum && k.FileType == f.Type) is { } existing)
            {
                ids[f.Field] = existing.Id;
                continue;
            }
            var uploaded = new UploadedFile
            {
                UploadedBy = startedBy.Value, FileType = f.Type, OriginalName = Path.GetFileName(f.File.FileName),
                Content = f.Content, MimeType = f.File.ContentType, SizeBytes = f.Content.Length,
                Checksum = f.Checksum, UploadedAt = now
            };
            db.UploadedFiles.Add(uploaded);
            ids[f.Field] = uploaded.Id;
        }

        var run = new ParserRun
        {
            StartedBy = startedBy.Value, PlanFileId = ids["plan"], CompListFileId = ids["competences"],
            CompMatrixFileId = ids["matrix"], EmployeesFileId = ids.TryGetValue("positions", out var p) ? p : null,
            StartedAt = now
        };
        db.ParserRuns.Add(run);
        await db.SaveChangesAsync(ct);

        jobs.Enqueue<ImportJob>(JobQueues.Parsing, job => job.RunAsync(run.Id, CancellationToken.None));
        return Results.Accepted($"/api/imports/{run.Id}", new ImportAccepted(run.Id));
    }

    private static async Task<IResult> Get(Guid id, AppDbContext db, CancellationToken ct)
    {
        var run = await db.ParserRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Запуск импорта не найден",
                detail: "Обновите список запусков на странице «Импорт данных».");

        var fileIds = new[] { run.PlanFileId, run.CompListFileId, run.CompMatrixFileId, run.EmployeesFileId ?? Guid.Empty };
        var names = await db.UploadedFiles.Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => f.OriginalName, ct);
        string? Name(Guid? fileId) => fileId is Guid g && names.TryGetValue(g, out var n) ? n : null;

        return Results.Ok(new ImportRunDetails(
            run.Id, run.Status, run.StartedAt, run.CompletedAt,
            new ImportCounts(run.ExtractedDisciplinesCount, run.ExtractedCompetencesCount, run.ExtractedEmployeesCount),
            new ImportFileNames(Name(run.PlanFileId), Name(run.CompListFileId), Name(run.CompMatrixFileId), Name(run.EmployeesFileId)),
            run.CreatedCurriculumPlanId,
            ImportIssue.Deserialize(run.ErrorsLog)));
    }

    private static async Task<IResult> List(AppDbContext db, CancellationToken ct)
    {
        var runs = await db.ParserRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct);
        return Results.Ok(runs.Select(r =>
        {
            var issues = ImportIssue.Deserialize(r.ErrorsLog);
            return new ImportRunItem(r.Id, r.Status, r.StartedAt, r.CompletedAt,
                new ImportCounts(r.ExtractedDisciplinesCount, r.ExtractedCompetencesCount, r.ExtractedEmployeesCount),
                issues.Count(i => i.Severity == ImportIssue.Error), issues.Count(i => i.Severity == ImportIssue.Warning));
        }).ToList());
    }
}

public record ImportAccepted(Guid Id);

public record ImportCounts(int Disciplines, int Competences, int Employees);

public record ImportFileNames(string? Plan, string? Competences, string? Matrix, string? Positions);

public record ImportRunDetails(
    Guid Id, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, ImportCounts Counts,
    ImportFileNames Files, Guid? CurriculumPlanId, IReadOnlyList<ImportIssue> Issues);

public record ImportRunItem(
    Guid Id, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, ImportCounts Counts,
    int Errors, int Warnings);
