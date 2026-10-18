namespace DisciplineDataExtractor;

/// <summary>Категория компетенции: УК, ОПК, ПК.</summary>
public enum CompetenceCategory { Universal, GeneralProfessional, Professional }

/// <summary>Тип индикатора по ФГОС: знать, уметь, владеть (иметь навыки, демонстрировать).</summary>
public enum IndicatorType { Knows, Can, Owns }

/// <summary>
/// Компетенция из списка компетенций. <see cref="CategoryName"/> — наименование группы
/// («Системное и критическое мышление»), в документах бывает только у УК.
/// </summary>
public sealed record Competence(
    string Code,
    CompetenceCategory Category,
    string? CategoryName,
    string Formulation,
    IReadOnlyList<CompetenceIndicator> Indicators);

/// <summary>Индикатор достижения: код (<c>УК-1.1</c>, <c>ПК-1.1.1</c>), текст без кода, тип; тип null — не распознан.</summary>
public sealed record CompetenceIndicator(string Code, string Text, IndicatorType? Type);

/// <summary>
/// Связь матрицы компетенций. <see cref="IndicatorCode"/> заполнен, если матрица задана
/// на уровне индикаторов; если на уровне компетенций — null.
/// </summary>
public sealed record MatrixLink(string DisciplineCode, string CompetenceCode, string? IndicatorCode);
