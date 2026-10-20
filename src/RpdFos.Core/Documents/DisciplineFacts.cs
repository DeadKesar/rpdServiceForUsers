using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>Индикатор — кандидат для выбора в документе.</summary>
public sealed record IndicatorCandidate(Guid Id, string Code, string Text, string Type);

/// <summary>Компетенция дисциплины из матрицы с индикаторами — кандидаты для выбора в разделе «Компетенции».</summary>
public sealed record CompetenceCandidate(Guid Id, string Code, string Formulation, string Category, IReadOnlyList<IndicatorCandidate> Indicators);

/// <summary>
/// Данные плана и справочников о дисциплине — всё, из чего предзаполняется документ и вычисляются условные разделы.
/// </summary>
public sealed record DisciplineFacts(
    Discipline Discipline, CurriculumPlan Plan, StudyDirection Direction, StudyProfile Profile, Department? Department,
    IReadOnlyList<CompetenceCandidate> Competences)
{
    public IReadOnlyList<DisciplineSemesterDetails> Semesters => Discipline.Semesters;

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

        return new DisciplineFacts(discipline, plan, direction, profile, department, competences
            .OrderBy(c => CategoryOrder(c.Category)).ThenBy(c => CodeKey(c.Code))
            .Select(c => new CompetenceCandidate(c.Id, c.Code, c.Formulation, c.Category, c.Indicators
                .OrderBy(i => CodeKey(i.Code))
                .Select(i => new IndicatorCandidate(i.Id, i.Code, i.Text, i.IndicatorType)).ToList()))
            .ToList());
    }

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
