using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>Индикатор — кандидат для выбора в документе.</summary>
public sealed record IndicatorCandidate(Guid Id, string Code, string Text, string Type);

/// <summary>Компетенция дисциплины из матрицы с индикаторами — кандидаты для выбора в разделе «Компетенции».</summary>
public sealed record CompetenceCandidate(Guid Id, string Code, string Formulation, string Category, IReadOnlyList<IndicatorCandidate> Indicators);

/// <summary>Дисциплина или практика того же плана — кандидат в пре- и постреквизиты: семестры изучения по плану.</summary>
public sealed record PlanDisciplineCandidate(Guid Id, string Code, string Name, IReadOnlyList<int> Semesters);

/// <summary>
/// Данные плана и справочников о дисциплине — всё, из чего предзаполняется документ и вычисляются условные разделы.
/// <paramref name="PlanDisciplines"/> — другие дисциплины и практики плана, которые можно назвать пре- и постреквизитами.
/// </summary>
public sealed record DisciplineFacts(
    Discipline Discipline, CurriculumPlan Plan, StudyDirection Direction, StudyProfile Profile, Department? Department,
    IReadOnlyList<CompetenceCandidate> Competences, IReadOnlyList<PlanDisciplineCandidate>? PlanDisciplines = null)
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
    /// Кандидат в пре- и постреквизиты: дисциплина (в том числе элективная), факультатив или практика с семестрами
    /// по плану. Модули и группы элективов — не предметы изучения, а группировки; ГИА — итог программы.
    /// </summary>
    public static bool IsPlanNeighbour(Discipline d) =>
        d.EntryKind is EntryKinds.Discipline or EntryKinds.Optional or EntryKinds.Practice &&
        d.PartType != PartTypes.ElectiveGroup && d.Block != PlanBlocks.FinalAttestation && d.Semesters.Count > 0;

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
