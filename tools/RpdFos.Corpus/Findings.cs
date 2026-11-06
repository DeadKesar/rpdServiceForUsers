namespace RpdFos.Corpus;

/// <summary>Классы находок (задание этапа 5.3, раздел D).</summary>
public static class FindingClasses
{
    public const string ParseError = "ошибка разбора", ModelGap = "пробел модели", FalsePositive = "ложное срабатывание",
        SourceData = "данные источника", DocumentVersion = "версия документа", Print = "печать", Expected = "ожидаемо";

    public static readonly string[] All = [ParseError, ModelGap, FalsePositive, SourceData, DocumentVersion, Print, Expected];
}

/// <summary>
/// Находка прогона: план или документ, вид находки, место, сообщение, класс. Класс ставится правилами инструмента —
/// это предложение для обсуждения, а не приговор: спорные случаи названы в сводке.
/// </summary>
public sealed record Finding(string Subject, string Kind, string Place, string Message, string Class);
