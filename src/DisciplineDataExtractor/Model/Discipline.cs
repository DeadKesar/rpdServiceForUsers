namespace DisciplineDataExtractor;

/// <summary>Блок плана: Б1 — дисциплины, Б2 — практики, Б3 — ГИА, ФТД — факультативы.</summary>
public enum PlanBlock { B1, B2, B3, Ftd }

/// <summary>Часть плана: обязательная, формируемая участниками, группа элективов и её элемент.</summary>
public enum PlanPart { Mandatory, Variable, ElectiveGroup, ElectiveItem }

public enum EntryKind { Discipline, Module, Practice, Gia, Optional }

/// <summary>Вид промежуточной аттестации: экзамен, зачёт, зачёт с оценкой, курсовой проект, курсовая работа.</summary>
public enum ControlType { Exam, Credit, GradedCredit, CourseworkProject, CourseworkPaper }

/// <summary>
/// Строка учебного плана. Модуль и группа элективов — тоже строки: их дочерние элементы
/// ссылаются на них через <see cref="ParentCode"/>.
/// </summary>
public sealed record Discipline(
    string Code,
    string Name,
    PlanBlock Block,
    PlanPart Part,
    EntryKind Kind,
    string? ParentCode,
    string? DepartmentCode,
    string? DepartmentName,
    int Credits,
    int TotalHours,
    IReadOnlyList<SemesterDetails> Semesters);

/// <summary>
/// Часы по видам занятий и аттестация в одном семестре. В семестре бывает несколько видов
/// аттестации (экзамен и курсовой проект), поэтому <see cref="ControlTypes"/> — список.
/// </summary>
public sealed record SemesterDetails(
    int Semester,
    int Credits,
    int Lectures,
    int Labs,
    int Practice,
    int SelfStudy,
    int Control,
    int ContactHours,
    IReadOnlyList<ControlType> ControlTypes);
