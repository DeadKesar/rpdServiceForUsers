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

    /// <summary>Учебный год начала изучения: год набора и первый семестр (1–2 — первый курс).</summary>
    public static string AcademicYear(int admissionYear, int firstSemester)
    {
        var start = admissionYear + (Math.Max(1, firstSemester) - 1) / 2;
        return $"{start}/{start + 1}";
    }
}
