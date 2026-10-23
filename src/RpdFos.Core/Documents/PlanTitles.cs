using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Названия кодов учебного плана для содержимого документа — в именительном падеже. Другие падежи для печати —
/// словарём форм на этапе 5 (решение по сверке B3 этапа 3).
/// </summary>
public static class PlanTitles
{
    public static string Block(string block) => block switch
    {
        PlanBlocks.Disciplines => "Блок 1 «Дисциплины (модули)»",
        PlanBlocks.Practice => "Блок 2 «Практика»",
        PlanBlocks.FinalAttestation => "Блок 3 «Государственная итоговая аттестация»",
        PlanBlocks.Optional => "ФТД «Факультативные дисциплины»",
        _ => block
    };

    // Дисциплина по выбору стоит в части, формируемой участниками образовательных отношений.
    public static string Part(string partType) => partType switch
    {
        PartTypes.Mandatory => "Обязательная часть",
        PartTypes.Variable or PartTypes.ElectiveGroup or PartTypes.ElectiveItem =>
            "Часть, формируемая участниками образовательных отношений",
        _ => partType
    };

    public static string EducationLevel(string level) => level switch
    {
        EducationLevels.Bachelor => "Бакалавриат",
        EducationLevels.Master => "Магистратура",
        EducationLevels.Specialist => "Специалитет",
        EducationLevels.Postgraduate => "Аспирантура",
        _ => level
    };

    public static string StudyForm(string form) => form switch
    {
        StudyForms.FullTime => "очная",
        StudyForms.PartTime => "заочная",
        StudyForms.Evening => "очно-заочная",
        _ => form
    };

    /// <summary>
    /// Квалификация — производная от уровня: явной квалификации в разобранном плане нет. Аспирантура — без
    /// квалификации в этом смысле, остаётся уровнем.
    /// </summary>
    public static string Qualification(string level) => level switch
    {
        EducationLevels.Bachelor => "бакалавр",
        EducationLevels.Master => "магистр",
        EducationLevels.Specialist => "специалист",
        _ => EducationLevel(level)
    };

    /// <summary>Курсовой проект или курсовая работа по видам аттестации плана; ни того ни другого — null.</summary>
    public static string? CourseWorkKind(IEnumerable<string> controlTypes)
    {
        var types = controlTypes.ToHashSet();
        return types.Contains(ControlTypeCodes.CourseProject) ? ControlTypeCodes.CourseProject
            : types.Contains(ControlTypeCodes.CourseWork) ? ControlTypeCodes.CourseWork
            : null;
    }

    /// <summary>
    /// Вид промежуточного контроля в таблицах трудоёмкости и разделов дисциплины: только экзамен, зачёт, зачёт
    /// с оценкой. Курсовой проект и курсовая работа печатаются в разделе 3.5.
    /// </summary>
    public static string Attestation(IEnumerable<string> controlTypes) => string.Join(", ", controlTypes
        .Where(t => t is ControlTypeCodes.Exam or ControlTypeCodes.Credit or ControlTypeCodes.GradedCredit)
        .Distinct()
        .Select(ControlType));

    /// <summary>Вид промежуточной аттестации — как в x-enum-titles схемы РПД.</summary>
    public static string ControlType(string type) => type switch
    {
        "exam" => "Экзамен",
        "credit" => "Зачёт",
        "graded_credit" => "Зачёт с оценкой",
        "course_project" => "Курсовой проект",
        "course_work" => "Курсовая работа",
        _ => type
    };

    /// <summary>Учебный год начала изучения: год набора и первый семестр (1–2 — первый курс).</summary>
    public static string AcademicYear(int admissionYear, int firstSemester)
    {
        var start = admissionYear + (Math.Max(1, firstSemester) - 1) / 2;
        return $"{start}/{start + 1}";
    }
}
