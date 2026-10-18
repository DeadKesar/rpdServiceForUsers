namespace DisciplineDataExtractor;

public enum EducationLevel { Unknown, Bachelor, Master, Specialist, Postgraduate }

/// <summary>Форма обучения: очная, заочная, очно-заочная.</summary>
public enum StudyForm { Unknown, FullTime, PartTime, Evening }

/// <summary>Сведения об учебном плане — лист «Титул» и справочник «Кафедры».</summary>
public sealed record CurriculumPlanInfo(
    string? DirectionCode,
    string? DirectionName,
    string? Profile,
    EducationLevel Level,
    StudyForm StudyForm,
    int? Year,
    string? Institute,
    string? ResponsibleDepartment,
    FgosInfo? Fgos,
    IReadOnlyList<DepartmentInfo> Departments);

/// <summary>Образовательный стандарт по титулу плана: «№ 932 от 19.09.2017».</summary>
public sealed record FgosInfo(string Order, string? OrderNumber, DateOnly? ApprovedOn);

/// <summary>Кафедра из листа «Кафедры»: <see cref="Code"/> — номер, на который ссылаются дисциплины.</summary>
public sealed record DepartmentInfo(string Code, string ShortName, string Name);
