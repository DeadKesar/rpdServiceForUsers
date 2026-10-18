namespace RpdFos.Core.Data.Entities;

/// <summary>Направление подготовки (таблица study_directions).</summary>
public class StudyDirection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Код направления — «09.04.04».</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string EducationLevel { get; set; } = EducationLevels.Bachelor;
    public Guid? ResponsibleDepartmentId { get; set; }
}

/// <summary>Профиль (направленность) направления подготовки (таблица study_profiles).</summary>
public class StudyProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DirectionId { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>ФГОС направления (таблица fgos). Естественный ключ — направление, номер и дата приказа.</summary>
public class Fgos
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DirectionId { get; set; }

    /// <summary>Версия стандарта («3++») — в исходных файлах её нет, заполняется вручную.</summary>
    public string? Version { get; set; }

    public string? OrderNumber { get; set; }
    public DateOnly ApprovedOn { get; set; }
    public string? DocumentUrl { get; set; }
    public bool IsCurrent { get; set; } = true;
}

public static class EducationLevels
{
    public const string Bachelor = "bachelor", Master = "master", Specialist = "specialist", Postgraduate = "postgraduate";
}
