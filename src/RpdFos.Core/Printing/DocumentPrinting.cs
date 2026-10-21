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

/// <summary>Итог формирования: задача, файл (если сформирован) и отчёт.</summary>
public sealed record PrintResult(GenerationTask Task, GeneratedFile? File, PrintReport Report);

/// <summary>
/// Формирование печатной формы документа по его шаблону: DOCX-шаблон оформления и сопоставление закладок —
/// из шаблона документа, подписанты — по его листу согласования (B4), содержимое — из документа. Файл ложится
/// в хранилище, запись — в generated_files со сроком хранения; задача и отчёт — в generation_tasks.
/// Сохраняет вызывающий.
/// </summary>
public sealed partial class DocumentPrinting(
    AppDbContext db, DocumentTypeRegistry registry, SignatoryResolution signatories, GeneratedFileStore store,
    IOptions<GenerationSettings> settings, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<PrintResult> GenerateDocxAsync(Document document, Guid requestedBy, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var task = db.GenerationTasks.Add(new GenerationTask
        {
            DocumentId = document.Id, Format = GenerationFormats.Docx, Status = GenerationStatuses.Running, RequestedBy = requestedBy,
            CreatedAt = now
        }).Entity;

        var template = await db.DocumentTemplates.IgnoreQueryFilters().SingleAsync(t => t.Id == document.TemplateId, ct);
        var content = template.PrintTemplateFileId is Guid fileId
            ? await db.UploadedFiles.Where(f => f.Id == fileId).Select(f => f.Content).SingleOrDefaultAsync(ct)
            : null;
        if (content is null || template.PrintMapping is null)
            return Fail(task, "У шаблона документа нет DOCX-шаблона оформления.");

        var facts = (await DisciplineFacts.LoadAsync(db, document.DisciplineId, ct))!;
        var schema = JsonDocument.Parse(template.Schema).RootElement;
        var seats = PrintMappingJson.ReadSignatures(template.SignaturesConfig);
        var context = new PrintContext(JsonDocument.Parse(document.Payload).RootElement, schema, registry.FindSchema, facts,
            await signatories.ResolveAsync(seats, document.DisciplineId, ct), Year(document),
            DocumentMetadata.Hidden(schema, facts, registry.FindSchema));

        var result = DocxGenerator.Generate(content, PrintMappingJson.ReadMapping(template.PrintMapping), context);
        var file = new GeneratedFile
        {
            GenerationTaskId = task.Id, Format = GenerationFormats.Docx, SizeBytes = result.Content.Length,
            FileName = FileName(document, facts.Discipline.Name, Year(document), "docx"), CreatedAt = now,
            ExpiresAt = now.AddDays(settings.Value.RetentionDays)
        };
        file.StoragePath = await store.SaveAsync(file.Id, "docx", result.Content, now, ct);
        db.GeneratedFiles.Add(file);

        task.Status = GenerationStatuses.Succeeded;
        task.Report = JsonSerializer.Serialize(result.Report, Json);
        task.CompletedAt = clock.GetUtcNow();
        return new PrintResult(task, file, result.Report);
    }

    public static PrintReport ReadReport(string? json) =>
        json is null ? new PrintReport([], []) : JsonSerializer.Deserialize<PrintReport>(json, Json) ?? new PrintReport([], []);

    private PrintResult Fail(GenerationTask task, string message)
    {
        task.Status = GenerationStatuses.Failed;
        task.ErrorMessage = message;
        task.CompletedAt = clock.GetUtcNow();
        return new PrintResult(task, null, new PrintReport([message], []));
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
