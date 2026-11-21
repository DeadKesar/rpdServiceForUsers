using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;

namespace RpdFos.Core.Documents;

/// <summary>Почему документ не создан.</summary>
public enum DocumentCreationProblem
{
    TemplateNotFound, TemplateNotPublished, TypeNotSupported, DisciplineNotFound, DisciplineNotInPlan, NotADiscipline, Duplicate,

    /// <summary>Дисциплина закреплена за другими преподавателями (этап 7, A10): автору создавать по ней нельзя — 403.</summary>
    DisciplineAssigned,

    /// <summary>Документ-основа не найден или удалён (этап 7, A11) — 422.</summary>
    BaseNotFound,

    /// <summary>Основа недоступна автору: не его документ и не документ закреплённой за ним дисциплины (A11) — 403.</summary>
    BaseNotAvailable,

    /// <summary>Основа другого типа, чем создаваемый документ (A11) — 422.</summary>
    BaseOtherType,

    /// <summary>
    /// Основа — документ другой дисциплины того же плана (A11): на основе создают для той же дисциплины или для дисциплины
    /// другого плана (решение 4) — 422.
    /// </summary>
    BaseSamePlan,

    /// <summary>ФОС: названная РПД не найдена, удалена или документ не РПД (этап 7, B3) — 422.</summary>
    RpdNotFound,

    /// <summary>ФОС: названная РПД — другой дисциплины, чем в запросе (B3) — 422.</summary>
    RpdOtherDiscipline,

    /// <summary>
    /// ФОС создаёт автор РПД или закреплённый за дисциплиной (B3, решение 5): у дисциплины без закреплений посторонний
    /// преподаватель — 403.
    /// </summary>
    RpdNotAvailable,

    /// <summary>РПД в архиве: ФОС по архивной РПД не создаётся (решение 8) — 409.</summary>
    RpdArchived,

    /// <summary>У дисциплины нет РПД, по которой можно создать ФОС: не создана, удалена или в архиве (B3) — 409.</summary>
    NoRpd,

    /// <summary>ФОС «на основе» другого документа появится вместе с циклом ФОС (B10) — 422.</summary>
    BaseNotSupported
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
/// таблицы и первая запись истории статусов — одним сохранением. «Создать на основе» (этап 7, A11) — то же создание, где
/// к предзаполнению из плана добавляется перенос ввода автора из документа-основы (<see cref="DocumentBasing"/>).
/// <para>
/// ФОС (этап 7, B3; FR-DOC-19, решения 5 и 8) создаётся по РПД той же дисциплины — не удалённой и не архивной, в любом другом
/// статусе. Создаёт автор РПД или закреплённый за дисциплиной (у дисциплины с закреплениями — только закреплённые, как у любого
/// документа, A10); методист и администратор — «за кафедру». К титулу из плана добавляется снимок данных РПД
/// (<see cref="FosSnapshot"/>), а составителем становится автор ФОС. Правило «один незавершённый документ на дисциплину и
/// линию редакций» действует и здесь: линия у ФОС своя, поэтому РПД и ФОС дисциплины друг другу не мешают.
/// </para>
/// </summary>
public sealed class DocumentCreation(
    AppDbContext db, DocumentTypeRegistry registry, DocumentProjections projections, DisciplineAssignments assignments,
    LinkedDocuments links, DocumentImages images, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="isCorrector">
    /// Создаёт методист или администратор — «за кафедру»: закрепления дисциплин (этап 7, A10) для них не ограничение, ограничены
    /// ими преподаватели. Закрепления определяют, кто из преподавателей создаёт документы дисциплины, а не кто их видит.
    /// Методист и администратор видят все документы, поэтому любой подходит им в основы (A11); остальным — свои и документы
    /// закреплённых за ними дисциплин.
    /// </param>
    /// <param name="basedOnId">
    /// Документ-основа (этап 7, A11; решение 4): неудалённый документ того же типа — для той же дисциплины или для дисциплины
    /// другого плана. У дисциплины с согласованным документом новый создаётся только на основе согласованного. ФОС на основе
    /// другого документа создаётся в цикле ФОС (B10): пока отказ.
    /// </param>
    /// <param name="rpdId">
    /// РПД, по которой создаётся ФОС (этап 7, B3). Не названа — берётся РПД дисциплины
    /// (<see cref="LinkedDocuments.DefaultRpdAsync"/>). У шаблона РПД не используется.
    /// </param>
    public async Task<DocumentCreationResult> CreateAsync(
        Guid templateId, Guid disciplineId, Guid authorId, bool isCorrector = false, Guid? basedOnId = null, Guid? rpdId = null,
        CancellationToken ct = default)
    {
        var template = await db.DocumentTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null) return new(null, DocumentCreationProblem.TemplateNotFound);
        if (template.Status != TemplateStatuses.Published)
            return new(null, DocumentCreationProblem.TemplateNotPublished,
                $"Шаблон в статусе «{TemplateStatuses.Title(template.Status)}»: документы создаются только по опубликованному.");
        if (template.Type is not (TemplateTypes.Rpd or TemplateTypes.Fos))
            return new(null, DocumentCreationProblem.TypeNotSupported, $"Документы типа «{template.Type}» не создаются.");
        var isFos = template.Type == TemplateTypes.Fos;

        var facts = await DisciplineFacts.LoadAsync(db, disciplineId, ct);
        if (facts is null) return new(null, DocumentCreationProblem.DisciplineNotFound);
        if (facts.Discipline.CurriculumPlanId != template.CurriculumPlanId)
            return new(null, DocumentCreationProblem.DisciplineNotInPlan, "Дисциплина не из учебного плана шаблона.");
        if (DocumentRules.WhyNoDocument(facts.Discipline) is { } reason)
            return new(null, DocumentCreationProblem.NotADiscipline, reason);

        // Закрепления — до проверки «документ уже есть»: чужому преподавателю закреплённая дисциплина отвечает отказом доступа,
        // а не рассказом о документе, к которому у него нет доступа (как «в архив» чужого документа, A9).
        IReadOnlyList<AssignmentHolder> holders = isCorrector ? [] : await assignments.HoldersAsync(disciplineId, ct);
        if (holders.Count > 0 && holders.All(h => h.EmployeeId != authorId))
            return new(null, DocumentCreationProblem.DisciplineAssigned, DisciplineAssignments.Refusal(holders));

        // РПД для ФОС (этап 7, B3). Порядок отказов: не найдена (422), недоступна (403), другой дисциплины (422), в архиве (409) —
        // доступ раньше данных, данные запроса раньше состояния. Без закреплений ФОС создаёт автор РПД; с закреплениями — только
        // закреплённые, это проверено выше.
        Document? rpd = null;
        if (isFos)
        {
            if (basedOnId is not null)
                return new(null, DocumentCreationProblem.BaseNotSupported,
                    "ФОС на основе другого документа создаётся в цикле ФОС и пока недоступен: создайте ФОС по РПД.");
            if (rpdId is Guid named)
            {
                rpd = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == named && d.Type == TemplateTypes.Rpd, ct);
                if (rpd is null)
                    return new(null, DocumentCreationProblem.RpdNotFound, "РПД не найдена: возможно, она удалена. ФОС создаётся по РПД.");
            }
            else
            {
                rpd = await links.DefaultRpdAsync(disciplineId, ct);
                if (rpd is null)
                    return new(null, DocumentCreationProblem.NoRpd,
                        "У дисциплины нет РПД, по которой можно создать ФОС: она не создана, удалена или в архиве. Сначала создайте РПД.");
            }
            if (!isCorrector && holders.Count == 0 && rpd.AuthorId != authorId)
                return new(null, DocumentCreationProblem.RpdNotAvailable,
                    "ФОС создаёт автор РПД или сотрудник, закреплённый за дисциплиной: обратитесь к методисту.");
            if (rpd.DisciplineId != disciplineId)
                return new(null, DocumentCreationProblem.RpdOtherDiscipline, "РПД относится к другой дисциплине, чем выбрана для ФОС.");
            if (rpd.Status == DocumentStatuses.Archived)
                return new(null, DocumentCreationProblem.RpdArchived, "РПД в архиве: ФОС по архивной РПД не создаётся.");
        }

        // Основа (этап 7, A11). Порядок отказов: не найдена (422), недоступна (403), другого типа (422), другая дисциплина
        // того же плана (422) — данные запроса раньше состояния дисциплины (409), доступ раньше данных.
        Document? basis = null;
        if (basedOnId is Guid basisId)
        {
            basis = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == basisId, ct);
            if (basis is null)
                return new(null, DocumentCreationProblem.BaseNotFound, "Документ-основа не найден: возможно, он удалён.");
            if (!isCorrector && basis.AuthorId != authorId && !await assignments.IsAssignedAsync(basis.DisciplineId, authorId, ct))
                return new(null, DocumentCreationProblem.BaseNotAvailable,
                    "Основой служит свой документ или документ дисциплины, закреплённой за вами.");
            if (basis.Type != template.Type)
                return new(null, DocumentCreationProblem.BaseOtherType, "Документ создаётся на основе документа того же типа.");
            var basisPlanId = await db.Disciplines.Where(d => d.Id == basis.DisciplineId).Select(d => d.CurriculumPlanId).SingleAsync(ct);
            if (basis.DisciplineId != disciplineId && basisPlanId == facts.Discipline.CurriculumPlanId)
                return new(null, DocumentCreationProblem.BaseSamePlan,
                    "Основа — документ другой дисциплины этого же плана. На основе создают документ той же дисциплины " +
                    "или дисциплины другого плана.");
        }

        // Один неудалённый документ на дисциплину и линию редакций шаблона. Архивный дисциплину не занимает (этап 7, A9,
        // решение 3): по ней создаётся новый. Незавершённый документ дисциплину занимает всегда; согласованный — пока новый не
        // создают на его основе (A11: у дисциплины с согласованным документом новый создаётся только так).
        var occupants = (await (
                    from d in db.Documents
                    join t in db.DocumentTemplates on d.TemplateId equals t.Id
                    where d.DisciplineId == disciplineId && t.LineageId == template.LineageId && d.Status != DocumentStatuses.Archived
                    select new { d.Id, d.Status, d.CreatedAt })
                .ToListAsync(ct))
            .OrderBy(o => DocumentTransitions.IsUnfinished(o.Status) ? 0 : 1).ThenByDescending(o => o.CreatedAt).ToList();
        if (occupants.FirstOrDefault() is { } occupant)
        {
            if (DocumentTransitions.IsUnfinished(occupant.Status))
                return new(null, DocumentCreationProblem.Duplicate, "Для этой дисциплины документ по шаблону уже есть.", occupant.Id);
            if (basis is null || occupants.All(o => o.Id != basis.Id))
                return new(null, DocumentCreationProblem.Duplicate,
                    "У дисциплины есть согласованный документ: новый создаётся на его основе («Создать на основе»).", occupant.Id);
        }

        var author = await db.Employees.AsNoTracking().Where(e => e.Id == authorId)
            .Select(e => new DocumentAuthor(e.Id, e.NameForDocument, e.Position)).SingleAsync(ct);
        var schema = JsonDocument.Parse(template.Schema).RootElement;
        var payload = DocumentPrefill.Build(schema, facts, author, registry.FindSchema);
        // Постоянные пункты вуза — из значений по умолчанию шаблона, кроме мест, не предусмотренных планом.
        var hidden = DocumentMetadata.Hidden(schema, facts.Semesters, registry.FindSchema);
        TemplateDefaults.Apply(payload, template.DefaultPayload, TemplateDefaults.Fields(schema, registry.FindSchema), hidden);

        var now = clock.GetUtcNow();
        // Титул — из плана (предзаполнение выше), наследуемое от РПД — снимком: связь, индикаторы, разделы, формы текущего
        // контроля, виды аттестации (этап 7, B3). Паспорт и оценочные средства ведёт автор ФОС.
        if (rpd is not null)
            FosSnapshot.Apply(payload, rpd.Id, rpd.Version, now, FosSnapshot.Take(JsonNode.Parse(rpd.Payload)));

        // Ввод автора основы — поверх предзаполненного для цели; что не перенесено, попадёт в аудит и в плашку редактора.
        IReadOnlyList<string> notTransferred = [];
        if (basis is not null)
        {
            var source = await db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == basis.TemplateId)
                .Select(t => new { t.Schema, t.DefaultPayload }).SingleAsync(ct);
            notTransferred = DocumentBasing.Transfer(payload, schema, JsonDocument.Parse(source.Schema).RootElement,
                source.DefaultPayload is { } defaults ? JsonNode.Parse(defaults) : null,
                JsonNode.Parse(basis.Payload) as JsonObject ?? [], facts, author.Id, hidden, registry.FindSchema).NotTransferred;
        }

        var document = new Document
        {
            Type = template.Type,
            TemplateId = template.Id,
            TemplateRevision = template.Revision,
            AuthorId = author.Id,
            DisciplineId = disciplineId,
            BasedOnDocumentId = basis?.Id,
            Title = Title(template.Type, facts),
            Payload = payload.ToJsonString(Json),
            CreatedAt = now
        };
        db.Documents.Add(document);
        // Рисунки основы — те же файлы: новому документу добавляются связи, файлы не копируются (этап 7, B4а).
        if (basis is not null) await images.LinkFromContentAsync(document, payload, ct);
        if (basis is not null)
            db.AuditLog.Add(new AuditLogEntry
            {
                UserId = author.Id, EventType = AuditEvents.DocumentBasedOn, EntityType = AuditEntities.Document,
                EntityId = document.Id, OccurredAt = now,
                Payload = new JsonObject
                {
                    ["base"] = basis.Id, ["title"] = basis.Title,
                    ["not_transferred"] = new JsonArray(notTransferred.Select(n => (JsonNode)n).ToArray())
                }.ToJsonString(Json)
            });
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
