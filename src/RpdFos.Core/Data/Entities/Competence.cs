namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Компетенция (таблица competences). УК и ОПК задаёт ФГОС — <see cref="ProfileId"/> пуст;
/// ПК задаёт образовательная программа — <see cref="ProfileId"/> указывает профиль.
/// <see cref="CategoryName"/> — группа, только у УК.
/// </summary>
public class Competence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FgosId { get; set; }

    /// <summary>Профиль — только у ПК (ограничение прототипа: ПК на профиль, а не на год набора).</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>Код — «УК-1», «ОПК-3», «ПК-5».</summary>
    public string Code { get; set; } = "";

    public string Category { get; set; } = CompetenceCategories.Universal;
    public string? CategoryName { get; set; }
    public string Formulation { get; set; } = "";
    public List<CompetenceIndicator> Indicators { get; set; } = [];
}

/// <summary>Индикатор достижения компетенции (таблица competence_indicators).</summary>
public class CompetenceIndicator
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompetenceId { get; set; }

    /// <summary>Код — «УК-1.1», у ПК до трёх уровней («ПК-2.1.3»).</summary>
    public string Code { get; set; } = "";

    public string Text { get; set; } = "";
    public string IndicatorType { get; set; } = IndicatorTypes.Knows;
}

public static class CompetenceCategories
{
    public const string Universal = "UK", GeneralProfessional = "OPK", Professional = "PK";
}

public static class IndicatorTypes
{
    // Знать / уметь / владеть (иметь навыки, демонстрировать) — по ФГОС.
    public const string Knows = "knows", Can = "can", Owns = "owns";
}
