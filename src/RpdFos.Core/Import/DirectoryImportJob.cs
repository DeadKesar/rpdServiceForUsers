using DisciplineDataExtractor;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Import;

/// <summary>
/// Фоновая загрузка файла должностей со страницы «Справочник сотрудников» (очередь parsing, этап 5.2): разбор и
/// правила этапа 2 (<see cref="EmployeeImport"/>) — добавить и обновить записи по официальному названию, смена
/// человека на должности, записи, которых нет в файле, не трогаются; кафедры — только из справочника кафедр плана.
/// Итог — счётчики и замечания в directory_imports. Без зависимости от Hangfire: очередь задаёт хост.
/// </summary>
public sealed class DirectoryImportJob(AppDbContext db, TimeProvider clock)
{
    public async Task RunAsync(Guid importId, CancellationToken ct)
    {
        var run = await db.DirectoryImports.SingleAsync(r => r.Id == importId, ct);
        if (run.Status != ParserRunStatuses.Running) return; // уже завершён — повторный запуск ничего не делает
        var file = await db.UploadedFiles.AsNoTracking().SingleAsync(f => f.Id == run.FileId, ct);

        var parsed = new DataExtractor().ExtractEmployees(new SourceFile(file.OriginalName, file.Content ?? []));
        var log = parsed.Issues.Select(ImportIssue.From).ToList();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var import = new EmployeeImport(db, clock.GetUtcNow(),
                m => log.Add(new ImportIssue(ImportIssue.Warning, file.OriginalName, null, null, m)));
            await import.LoadDepartmentsAsync(ct);
            await import.UpsertInstitutesAsync(null, parsed.Employees, ct);
            foreach (var department in import.DepartmentsByName.Values) import.LinkInstitute(department, parsed.Employees);
            var counts = await import.UpsertEmployeesAsync(parsed.Employees, ct);

            run.RowsCount = parsed.Employees.Count;
            (run.AddedCount, run.UpdatedCount, run.ReplacedCount) = (counts.Added, counts.Updated, counts.Replaced);
            run.Status = parsed.Employees.Count == 0 ? ParserRunStatuses.Failed
                : log.Any(i => i.Severity == ImportIssue.Error) ? ParserRunStatuses.Partial : ParserRunStatuses.Succeeded;
            if (parsed.Employees.Count == 0)
                log.Add(new ImportIssue(ImportIssue.Error, file.OriginalName, null, null, "В файле не найдено ни одной строки должностей"));
            run.ErrorsLog = ImportIssue.Serialize(log);
            run.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Нарушение ограничения БД: транзакция откачена, итог — failed вне неё.
            db.ChangeTracker.Clear();
            log.Add(new ImportIssue(ImportIssue.Error, file.OriginalName, null, null, $"Загрузка прервана: {e.GetBaseException().Message}"));
            var failed = await db.DirectoryImports.SingleAsync(r => r.Id == importId, ct);
            failed.Status = ParserRunStatuses.Failed;
            failed.ErrorsLog = ImportIssue.Serialize(log);
            failed.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }
}
