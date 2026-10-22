using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Printing;

/// <summary>
/// Формирование печатной формы документа по его шаблону: DOCX-шаблон оформления и сопоставление закладок —
/// из шаблона документа, подписанты — по его листу согласования, содержимое — из документа; PDF — конвертацией
/// сформированного DOCX (FR-GEN-02). Задача ставится в очередь (<see cref="Queue"/>) и выполняется в фоне
/// (<see cref="RunAsync"/>, FR-GEN-04): статус и отчёт — в generation_tasks, файл — в хранилище и generated_files
/// со сроком хранения (FR-GEN-06). Сохраняет вызывающий.
/// </summary>
public sealed partial class DocumentPrinting(
    AppDbContext db, DocumentTypeRegistry registry, SignatoryResolution signatories, GeneratedFileStore store,
    IPdfConverter converter, IOptions<GenerationSettings> settings, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Новая задача формирования в статусе «в очереди».</summary>
    public GenerationTask Queue(Document document, string format, Guid requestedBy) =>
        db.GenerationTasks.Add(new GenerationTask
        {
            DocumentId = document.Id, Format = format, Status = GenerationStatuses.Queued, RequestedBy = requestedBy,
            CreatedAt = clock.GetUtcNow()
        }).Entity;

    /// <summary>
    /// Выполнение задачи: DOCX, для PDF — конвертация; файл — в хранилище. Ошибка конвертера или шаблона
    /// завершает задачу со статусом failed и причиной — без исключения наружу.
    /// </summary>
    public async Task RunAsync(GenerationTask task, CancellationToken ct = default)
    {
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == task.DocumentId, ct);
        var template = await db.DocumentTemplates.IgnoreQueryFilters().SingleAsync(t => t.Id == document.TemplateId, ct);
        var content = template.PrintTemplateFileId is Guid fileId
            ? await db.UploadedFiles.Where(f => f.Id == fileId).Select(f => f.Content).SingleOrDefaultAsync(ct)
            : null;
        if (content is null || template.PrintMapping is null)
        {
            Fail(task, "У шаблона документа нет DOCX-шаблона оформления.");
            return;
        }

        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        var schema = JsonDocument.Parse(template.Schema).RootElement;
        var seats = PrintMappingJson.ReadSignatures(template.SignaturesConfig);
        var context = new PrintContext(JsonDocument.Parse(document.Payload).RootElement, schema, registry.FindSchema, facts,
            await signatories.ResolveAsync(seats, document.DisciplineId, ct), Year(document),
            DocumentMetadata.Hidden(schema, facts, registry.FindSchema));
        var result = DocxGenerator.Generate(content, PrintMappingJson.ReadMapping(template.PrintMapping), context);
        task.Report = JsonSerializer.Serialize(result.Report, Json);

        var bytes = result.Content;
        if (task.Format == GenerationFormats.Pdf)
            try
            {
                bytes = await converter.ConvertAsync(result.Content, ct);
            }
            catch (PdfConversionException e)
            {
                Fail(task, e.Message);
                return;
            }

        var now = clock.GetUtcNow();
        var file = new GeneratedFile
        {
            GenerationTaskId = task.Id, Format = task.Format, SizeBytes = bytes.Length,
            FileName = FileName(document, facts.Discipline.Name, Year(document), task.Format), CreatedAt = now,
            ExpiresAt = now.AddDays(settings.Value.RetentionDays)
        };
        file.StoragePath = await store.SaveAsync(file.Id, task.Format, bytes, now, ct);
        db.GeneratedFiles.Add(file);
        task.Status = GenerationStatuses.Succeeded;
        task.CompletedAt = clock.GetUtcNow();
    }

    public static PrintReport ReadReport(string? json) =>
        json is null ? new PrintReport([], []) : JsonSerializer.Deserialize<PrintReport>(json, Json) ?? new PrintReport([], []);

    private void Fail(GenerationTask task, string message)
    {
        task.Status = GenerationStatuses.Failed;
        task.ErrorMessage = message;
        task.CompletedAt = clock.GetUtcNow();
    }

    // Год документа — год утверждения, до него — год создания: перепечатка не меняет год на титуле.
    private static int Year(Document document) => (document.ApprovedAt ?? document.CreatedAt).Year;

    /// <summary>Имя файла по шаблону «Тип_Дисциплина_Год» (FR-GEN-03): «РПД_Программирование_2025.docx».</summary>
    public static string FileName(Document document, string discipline, int year, string extension)
    {
        var type = document.Type == TemplateTypes.Fos ? "ФОС" : "РПД";
        var name = Spaces().Replace(InvalidChars().Replace(discipline, ""), " ").Trim();
        return $"{type}_{name}_{year}.{extension}";
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]")]
    private static partial Regex InvalidChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Фоновая задача формирования (очередь generation): «в очереди» → «выполняется» → «готово» или «ошибка».
/// Без зависимости от Hangfire: очередь и запуск задаёт хост.
/// </summary>
public sealed class GenerationJob(AppDbContext db, DocumentPrinting printing, TimeProvider clock)
{
    public async Task RunAsync(Guid taskId, CancellationToken ct)
    {
        var task = await db.GenerationTasks.SingleAsync(t => t.Id == taskId, ct);
        if (task.Status != GenerationStatuses.Queued) return; // уже выполнена — повторный запуск ничего не делает
        task.Status = GenerationStatuses.Running;
        await db.SaveChangesAsync(ct);

        try
        {
            await printing.RunAsync(task, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Причина — пользователю в статусе задачи; без неё задача навсегда осталась бы «выполняется».
            task.Status = GenerationStatuses.Failed;
            task.ErrorMessage = $"Документ не сформирован: {e.Message}";
            task.CompletedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Регулярная очистка (FR-GEN-06): файлы с истёкшим сроком хранения удаляются с диска и из generated_files.</summary>
public sealed class GeneratedFilesCleanup(AppDbContext db, GeneratedFileStore store, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await db.GeneratedFiles.Where(f => f.ExpiresAt <= now).ToListAsync(ct);
        foreach (var file in expired) store.Delete(file.StoragePath);
        db.GeneratedFiles.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
