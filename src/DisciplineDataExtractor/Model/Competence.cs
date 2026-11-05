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

/// <summary>
/// Строка матрицы с отметками, не сопоставленная с дисциплиной плана: не найдена по названию или неоднозначна
/// (<see cref="Candidates"/> — индексы кандидатов). Возвращается данными, чтобы методист привязал её вручную:
/// иначе её связи с компетенциями теряются. <see cref="Codes"/> — отметки как в матрице (компетенция или
/// индикатор), <see cref="CompetenceCodes"/> — компетенции этих отметок.
/// </summary>
public sealed record UnmatchedMatrixRow(
    string Sheet, int Row, string Name, string Semesters, IReadOnlyList<string> Codes, IReadOnlyList<string> CompetenceCodes,
    IReadOnlyList<string> Candidates);
