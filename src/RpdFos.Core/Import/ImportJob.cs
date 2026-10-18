using DisciplineDataExtractor;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Import;

/// <summary>
/// Фоновая задача импорта: файлы запуска из uploaded_files → разбор → конвейер импорта.
/// Без зависимости от Hangfire: очередь и запуск задаёт хост.
/// </summary>
public sealed class ImportJob(AppDbContext db, ReferenceImporter importer)
{
    public async Task RunAsync(Guid parserRunId, CancellationToken ct)
    {
        var run = await db.ParserRuns.AsNoTracking().SingleAsync(r => r.Id == parserRunId, ct);
        if (run.Status != ParserRunStatuses.Running) return; // уже завершён — повторный запуск задачи ничего не делает

        var ids = new List<Guid> { run.PlanFileId, run.CompListFileId, run.CompMatrixFileId };
        if (run.EmployeesFileId is Guid employeesId) ids.Add(employeesId);
        var files = await db.UploadedFiles.AsNoTracking().Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

        // Пустое содержимое (файл не в базе) разбор отметит ошибкой «Файл не прочитан».
        SourceFile Source(Guid id) => new(files[id].OriginalName, files[id].Content ?? []);
        var input = new ExtractionInput(Source(run.PlanFileId), Source(run.CompListFileId), Source(run.CompMatrixFileId),
            run.EmployeesFileId is Guid e ? Source(e) : null);

        var result = new DataExtractor().Extract(input);
        await importer.ImportAsync(run.Id, result, ct);
    }
}
