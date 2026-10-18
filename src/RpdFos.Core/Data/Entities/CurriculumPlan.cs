namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Учебный план (таблица curriculum_plans). Каждая загрузка создаёт новую версию: номер версии —
/// следующий для того же направления, профиля, года набора и формы обучения.
/// </summary>
public class CurriculumPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DirectionId { get; set; }
    public Guid ProfileId { get; set; }

    /// <summary>Год набора.</summary>
    public int Year { get; set; }

    public string StudyForm { get; set; } = StudyForms.FullTime;
    public int Version { get; set; } = 1;
    public Guid? SourceFileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Discipline> Disciplines { get; set; } = [];
}

/// <summary>
/// Строка учебного плана (таблица disciplines): дисциплина, модуль, группа элективов, практика, ГИА,
/// факультатив. Иерархия — через <see cref="ParentId"/>: модуль и группа элективов — родители.
/// </summary>
public class Discipline
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CurriculumPlanId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ParentId { get; set; }
    public Discipline? Parent { get; set; }

    /// <summary>Индекс по учебному плану — «Б1.О.01».</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string Block { get; set; } = PlanBlocks.Disciplines;
    public string PartType { get; set; } = PartTypes.Mandatory;
    public string EntryKind { get; set; } = EntryKinds.Discipline;
    public int TotalHours { get; set; }
    public int TotalCredits { get; set; }
    public List<DisciplineSemesterDetails> Semesters { get; set; } = [];
}

/// <summary>
/// Часы и аттестация дисциплины в семестре (таблица discipline_semester_details).
/// Видов аттестации в семестре может быть несколько (экзамен и курсовой проект) — отступление
/// от проекта, согласованное на этапе 2: <c>control_types text[]</c> вместо одного <c>control_type</c>.
/// </summary>
public class DisciplineSemesterDetails
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DisciplineId { get; set; }
    public int Semester { get; set; }
    public int ContactHours { get; set; }
    public int Lectures { get; set; }
    public int Practice { get; set; }
    public int LabHours { get; set; }
    public int SelfStudy { get; set; }
    public int Control { get; set; }
    public int Credits { get; set; }
    public List<string> ControlTypes { get; set; } = [];
}

/// <summary>Связь «дисциплина — компетенция» матрицы компетенций (таблица competence_matrix).</summary>
public class CompetenceMatrixEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DisciplineId { get; set; }
    public Guid CompetenceId { get; set; }
}

public static class StudyForms
{
    public const string FullTime = "full_time", PartTime = "part_time", Evening = "evening";
}

public static class PlanBlocks
{
    // Б1 — дисциплины, Б2 — практики, Б3 — ГИА, ФТД — факультативы.
    public const string Disciplines = "B1", Practice = "B2", FinalAttestation = "B3", Optional = "FTD";
}

public static class PartTypes
{
    public const string Mandatory = "mandatory", Variable = "variable",
        ElectiveGroup = "elective_group", ElectiveItem = "elective_item";
}

public static class EntryKinds
{
    public const string Discipline = "discipline", Module = "module", Practice = "practice", Gia = "gia", Optional = "optional";
}

public static class ControlTypeCodes
{
    public const string Exam = "exam", Credit = "credit", GradedCredit = "graded_credit",
        CourseProject = "course_project", CourseWork = "course_work";

    public static readonly string[] All = [Exam, Credit, GradedCredit, CourseProject, CourseWork];
}
