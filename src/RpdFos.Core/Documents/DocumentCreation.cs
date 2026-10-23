using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;

namespace RpdFos.Core.Documents;

/// <summary>Почему документ не создан.</summary>
public enum DocumentCreationProblem
{
    TemplateNotFound, TemplateNotPublished, TypeNotSupported, DisciplineNotFound, DisciplineNotInPlan, NotADiscipline, Duplicate
}

public sealed record DocumentCreationResult(Document? Document, DocumentCreationProblem? Problem = null, string? Detail = null,
    Guid? ExistingId = null);

/// <summary>Для каких строк учебного плана создаются документы (FR-DOC-18).</summary>
public static class DocumentRules
{
    /// <summary>Причина, по которой для строки плана документ не создаётся; null — создаётся.</summary>
    public static string? WhyNoDocument(Discipline d) => d switch
    {
        { EntryKind: EntryKinds.Module } =>
            $"«{d.Name}» — модуль, а не дисциплина: РПД создаётся для каждой дисциплины модуля.",
        { EntryKind: EntryKinds.Practice } or { Block: PlanBlocks.Practice } =>
            $"«{d.Name}» — практика (Б2): программы практик в прототипе не создаются (FR-DOC-18).",
        { EntryKind: EntryKinds.Gia } or { Block: PlanBlocks.FinalAttestation } =>
            $"«{d.Name}» — государственная итоговая аттестация (Б3): программа ГИА в прототипе не создаётся (FR-DOC-18).",
        // У группы элективов в плане тот же вид строки, что у дисциплины, — отличает часть плана.
        { PartType: PartTypes.ElectiveGroup } =>
            $"«{d.Name}» — группа дисциплин по выбору: РПД создаётся для каждой дисциплины группы.",
        { EntryKind: EntryKinds.Discipline or EntryKinds.Optional } => null,
        _ => $"«{d.Name}» — не дисциплина учебного плана."
    };

    public static bool CanHaveDocument(Discipline d) => WhyNoDocument(d) is null;
}

/// <summary>
/// Создание документа по опубликованному шаблону для дисциплины его плана: проверки, предзаполнение, нормализованные
/// таблицы и первая запись истории статусов — одним сохранением.
/// </summary>
public sealed class DocumentCreation(AppDbContext db, DocumentTypeRegistry registry, DocumentProjections projections, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<DocumentCreationResult> CreateAsync(Guid templateId, Guid disciplineId, Guid authorId, CancellationToken ct = default)
    {
        var template = await db.DocumentTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null) return new(null, DocumentCreationProblem.TemplateNotFound);
        if (template.Status != TemplateStatuses.Published)
            return new(null, DocumentCreationProblem.TemplateNotPublished,
                $"Шаблон в статусе «{TemplateStatuses.Title(template.Status)}»: документы создаются только по опубликованному.");
        if (template.Type != TemplateTypes.Rpd)
            return new(null, DocumentCreationProblem.TypeNotSupported, "ФОС создаются на этапе 6; сейчас — только РПД.");

        var facts = await DisciplineFacts.LoadAsync(db, disciplineId, ct);
        if (facts is null) return new(null, DocumentCreationProblem.DisciplineNotFound);
        if (facts.Discipline.CurriculumPlanId != template.CurriculumPlanId)
            return new(null, DocumentCreationProblem.DisciplineNotInPlan, "Дисциплина не из учебного плана шаблона.");
        if (DocumentRules.WhyNoDocument(facts.Discipline) is { } reason)
            return new(null, DocumentCreationProblem.NotADiscipline, reason);

        // Один неудалённый документ на дисциплину и линию редакций шаблона.
        var existing = await (
                from d in db.Documents
                join t in db.DocumentTemplates on d.TemplateId equals t.Id
                where d.DisciplineId == disciplineId && t.LineageId == template.LineageId
                select (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is Guid existingId)
            return new(null, DocumentCreationProblem.Duplicate, "Для этой дисциплины документ по шаблону уже есть.", existingId);

        var author = await db.Employees.AsNoTracking().Where(e => e.Id == authorId)
            .Select(e => new DocumentAuthor(e.Id, e.NameForDocument, e.Position)).SingleAsync(ct);
        var schema = JsonDocument.Parse(template.Schema).RootElement;
        var payload = DocumentPrefill.Build(schema, facts, author, registry.FindSchema);
        // Постоянные пункты вуза — из значений по умолчанию шаблона, кроме мест, не предусмотренных планом.
        TemplateDefaults.Apply(payload, template.DefaultPayload, TemplateDefaults.Fields(schema, registry.FindSchema),
            DocumentMetadata.Hidden(schema, facts, registry.FindSchema));

        var now = clock.GetUtcNow();
        var document = new Document
        {
            Type = template.Type,
            TemplateId = template.Id,
            TemplateRevision = template.Revision,
            AuthorId = author.Id,
            DisciplineId = disciplineId,
            Title = Title(template.Type, facts),
            Payload = payload.ToJsonString(Json),
            CreatedAt = now
        };
        db.Documents.Add(document);
        await projections.SyncAsync(document, ct);
        db.DocumentStatusHistory.Add(new DocumentStatusChange
        {
            DocumentId = document.Id, ToStatus = document.Status, ChangedBy = author.Id, ChangedAt = now
        });
        await db.SaveChangesAsync(ct);
        return new(document);
    }

    /// <summary>«Тип + дисциплина + год»: «РПД «Математическое и компьютерное моделирование», набор 2023».</summary>
    public static string Title(string type, DisciplineFacts facts) =>
        $"{(type == TemplateTypes.Fos ? "ФОС" : "РПД")} «{facts.Discipline.Name}», набор {facts.Plan.Year}";
}
