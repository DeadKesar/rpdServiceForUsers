using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>Индикатор — кандидат для выбора в документе.</summary>
public sealed record IndicatorCandidate(Guid Id, string Code, string Text, string? Type);

/// <summary>Компетенция дисциплины из матрицы с индикаторами — кандидаты для выбора в разделе «Компетенции».</summary>
public sealed record CompetenceCandidate(Guid Id, string Code, string Formulation, string Category, IReadOnlyList<IndicatorCandidate> Indicators);

/// <summary>Дисциплина или практика того же плана — кандидат в пре- и постреквизиты: семестры изучения по плану.</summary>
public sealed record PlanDisciplineCandidate(Guid Id, string Code, string Name, IReadOnlyList<int> Semesters);

/// <summary>
/// РПД, по которой создан ФОС, для правил сквозной проверки (этап 7, B8): статус и текущие наследуемые данные —
/// те же поля, что в снимке ФОС (<see cref="FosSnapshot.Take"/>), но по нынешнему содержимому РПД. <paramref name="Deleted"/> —
/// РПД удалена (запись остаётся). <paramref name="EnforceStatus"/> — статус РПД блокирует переход ФОС: ФОС ещё не принят;
/// у принятого и дальше правило ничего не требует — иначе архивирование РПД остановило бы согласование уже принятого ФОС.
/// </summary>
public sealed record LinkedRpdInfo(Guid Id, string Status, bool Deleted, JsonObject Current, bool EnforceStatus);

/// <summary>
/// Данные плана и справочников о дисциплине — всё, из чего предзаполняется документ и вычисляются условные разделы.
/// <paramref name="PlanDisciplines"/> — другие дисциплины и практики плана, которые можно назвать пре- и постреквизитами.
/// <paramref name="LinkedRpd"/> — у ФОС связанная РПД: правилам сквозной проверки она нужна как есть сейчас, а не снимком.
/// </summary>
public sealed record DisciplineFacts(
    Discipline Discipline, CurriculumPlan Plan, StudyDirection Direction, StudyProfile Profile, Department? Department,
    IReadOnlyList<CompetenceCandidate> Competences, IReadOnlyList<PlanDisciplineCandidate>? PlanDisciplines = null,
    LinkedRpdInfo? LinkedRpd = null)
{
    public IReadOnlyList<DisciplineSemesterDetails> Semesters => Discipline.Semesters;

    /// <summary>Дисциплины и практики, которые изучаются раньше первого семестра дисциплины (2.1, пререквизиты).</summary>
    public IReadOnlyList<PlanDisciplineCandidate> Earlier => Semesters.Count == 0
        ? []
        : (PlanDisciplines ?? []).Where(c => c.Semesters.Max() < Semesters.Min(s => s.Semester)).ToList();

    /// <summary>Дисциплины и практики, которые изучаются позже последнего семестра дисциплины (2.2, постреквизиты).</summary>
    public IReadOnlyList<PlanDisciplineCandidate> Later => Semesters.Count == 0
        ? []
        : (PlanDisciplines ?? []).Where(c => c.Semesters.Min() > Semesters.Max(s => s.Semester)).ToList();

    /// <summary>
    /// Формулировка для поля выбора из плана (<c>x-plan-disciplines</c> в <paramref name="fieldSchema"/>), когда выбирать
    /// не из чего: у дисциплины нет предшественников (или последователей) в плане. Текст — из сопоставления печати
    /// (<paramref name="texts"/>, по коду уровня образования направления); кандидаты есть — null.
    /// </summary>
    public string? NoCandidatesText(JsonElement fieldSchema, IReadOnlyDictionary<string, string>? texts)
    {
        if (texts is null || fieldSchema.ValueKind != JsonValueKind.Object ||
            !fieldSchema.TryGetProperty("x-plan-disciplines", out var relation)) return null;
        var candidates = relation.GetString() == "later" ? Later : Earlier;
        return candidates.Count == 0 ? texts.GetValueOrDefault(Direction.EducationLevel) : null;
    }

    /// <summary>
    /// Кандидат в пре- и постреквизиты: дисциплина (в том числе элективная), факультатив или практика с семестрами
    /// по плану. Модули и группы элективов — не предметы изучения, а группировки; ГИА — итог программы.
    /// </summary>
    public static bool IsPlanNeighbour(Discipline d) =>
        d.EntryKind is EntryKinds.Discipline or EntryKinds.Optional or EntryKinds.Practice &&
        d.PartType != PartTypes.ElectiveGroup && d.Block != PlanBlocks.FinalAttestation && d.Semesters.Count > 0;

    // Статусы ФОС, в которых связанная РПД ещё может не пустить документ дальше: правка, возврат и проверка.
    private static readonly string[] Gated =
        [DocumentStatuses.Draft, DocumentStatuses.SavedDraft, DocumentStatuses.Returned, DocumentStatuses.UnderReview];

    /// <summary>
    /// Факты документа: дисциплина и, у ФОС, связанная РПД. Удалённую РПД читаем (фильтр запросов отключён): ФОС должен знать,
    /// что она удалена. РПД, которой нет в базе, — <c>LinkedRpd</c> пуст.
    /// </summary>
    public static async Task<DisciplineFacts?> LoadForAsync(AppDbContext db, Document document, CancellationToken ct = default)
    {
        var facts = await LoadAsync(db, document.DisciplineId, ct);
        if (facts is null || document.Type != TemplateTypes.Fos || FosSnapshot.RpdIdOf(document.Payload) is not { } rpdId) return facts;

        var rpd = await db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Id == rpdId).Select(d => new { d.Id, d.Status, d.IsDeleted, d.Payload }).SingleOrDefaultAsync(ct);
        if (rpd is null) return facts;
        var current = FosSnapshot.Take(JsonNode.Parse(rpd.Payload));
        var deleted = rpd.IsDeleted || rpd.Status == DocumentStatuses.Deleted;
        return facts with { LinkedRpd = new LinkedRpdInfo(rpd.Id, rpd.Status, deleted, current, Gated.Contains(document.Status)) };
    }

    public static async Task<DisciplineFacts?> LoadAsync(AppDbContext db, Guid disciplineId, CancellationToken ct = default)
    {
        var discipline = await db.Disciplines.AsNoTracking().Include(d => d.Semesters)
            .SingleOrDefaultAsync(d => d.Id == disciplineId, ct);
        if (discipline is null) return null;
        discipline.Semesters = discipline.Semesters.OrderBy(s => s.Semester).ToList();

        var plan = await db.CurriculumPlans.AsNoTracking().SingleAsync(p => p.Id == discipline.CurriculumPlanId, ct);
        var direction = await db.StudyDirections.AsNoTracking().SingleAsync(d => d.Id == plan.DirectionId, ct);
        var profile = await db.StudyProfiles.AsNoTracking().SingleAsync(p => p.Id == plan.ProfileId, ct);
        var department = discipline.DepartmentId is Guid departmentId
            ? await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId, ct)
            : null;

        var competences = await (
                from m in db.CompetenceMatrix
                where m.DisciplineId == disciplineId
                join c in db.Competences.Include(c => c.Indicators) on m.CompetenceId equals c.Id
                select c)
            .AsNoTracking().ToListAsync(ct);

        var neighbours = (await db.Disciplines.AsNoTracking().Include(d => d.Semesters)
                .Where(d => d.CurriculumPlanId == discipline.CurriculumPlanId && d.Id != disciplineId)
                .ToListAsync(ct))
            .Where(IsPlanNeighbour)
            // Как в учебном плане: блок, обязательная часть раньше вариативной, затем индекс.
            .OrderBy(d => Array.IndexOf(BlockOrder, d.Block)).ThenBy(d => d.PartType == PartTypes.Mandatory ? 0 : 1)
            .ThenBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => new PlanDisciplineCandidate(d.Id, d.Code, d.Name, d.Semesters.Select(s => s.Semester).Order().ToList()))
            .ToList();

        return new DisciplineFacts(discipline, plan, direction, profile, department, competences
            .OrderBy(c => CategoryOrder(c.Category)).ThenBy(c => CodeKey(c.Code))
            .Select(c => new CompetenceCandidate(c.Id, c.Code, c.Formulation, c.Category, c.Indicators
                .OrderBy(i => CodeKey(i.Code))
                .Select(i => new IndicatorCandidate(i.Id, i.Code, i.Text, i.IndicatorType)).ToList()))
            .ToList(), neighbours);
    }

    private static readonly string[] BlockOrder = [PlanBlocks.Disciplines, PlanBlocks.Practice, PlanBlocks.FinalAttestation, PlanBlocks.Optional];

    private static int CategoryOrder(string category) => category switch
    {
        CompetenceCategories.Universal => 0,
        CompetenceCategories.GeneralProfessional => 1,
        _ => 2
    };

    // «ПК-1.10» после «ПК-1.9»: номера сравниваются как числа.
    private static string CodeKey(string code) =>
        string.Join('.', code[(code.IndexOf('-') + 1)..].Split('.').Select(n => int.TryParse(n, out var i) ? i.ToString("D4") : n));
}
