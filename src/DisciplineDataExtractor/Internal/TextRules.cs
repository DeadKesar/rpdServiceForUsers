using System.Text.RegularExpressions;

namespace DisciplineDataExtractor.Internal;

/// <summary>Нормализация текста и коды компетенций. Только неизменяемые регулярные выражения.</summary>
internal static partial class TextRules
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Для ключа сравнения названий: дефисы и тире, кавычки, скобки и пунктуация не различают названия.
    [GeneratedRegex(@"[\-‐‑‒–—―«»""“”„'()\[\].,;:!?]")]
    private static partial Regex NamePunctuation();

    // Код компетенции или индикатора в начале строки: «УК-1», «ОПК-2.3», «ПК-1.1.1».
    // «З» вместо «3» — частая опечатка в документах; буква после кода означает, что это не код.
    [GeneratedRegex(@"^(?<cat>УК|ОПК|ПК)\s*-\s*(?<num>[\dЗ]{1,3})(?<rest>(?:\.[\dЗ]{1,3})*)(?![\dа-яё])")]
    private static partial Regex CodeAtStart();

    // Начало следующего индикатора внутри абзаца: код с точкой и пробелом после него.
    [GeneratedRegex(@"(?<=\s)(?=(?:УК|ОПК|ПК)-\d{1,3}(?:\.\d{1,3})+\.\s)")]
    private static partial Regex InlineIndicatorStart();

    [GeneratedRegex(@"\d{2}\.\d{2}\.\d{2}")]
    public static partial Regex DirectionCode();

    /// <summary>Пробелы и переводы строк — в один пробел, по краям — без пробелов.</summary>
    public static string Clean(string? text) => Spaces().Replace(text ?? "", " ").Trim();

    /// <summary>Ключ заголовка колонки: без регистра, «ё» и пробелов («Экза мен» = «экзамен»).</summary>
    public static string HeaderKey(string? text) =>
        Spaces().Replace(text ?? "", "").ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Ключ сравнения названий дисциплин между планом и матрицей.</summary>
    public static string NameKey(string? text) =>
        Clean(NamePunctuation().Replace((text ?? "").ToLowerInvariant().Replace('ё', 'е'), " "));

    /// <summary>Слова названия без учёта порядка и повторов — для сопоставления по словам.</summary>
    public static string NameTokensKey(string? text) =>
        string.Join(' ', NameKey(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Order(StringComparer.Ordinal));

    /// <summary>Разбирает код в начале строки. <paramref name="tail"/> — текст после кода без разделителей.</summary>
    public static bool TryParseCode(string text, out CompetenceCode code, out string tail)
    {
        var m = CodeAtStart().Match(text);
        if (!m.Success)
        {
            code = default;
            tail = text;
            return false;
        }

        var category = m.Groups["cat"].Value switch
        {
            "УК" => CompetenceCategory.Universal,
            "ОПК" => CompetenceCategory.GeneralProfessional,
            _ => CompetenceCategory.Professional
        };
        var competence = $"{m.Groups["cat"].Value}-{m.Groups["num"].Value.Replace('З', '3')}";
        var rest = m.Groups["rest"].Value.Replace('З', '3');
        code = new CompetenceCode(category, competence, rest.Length == 0 ? null : competence + rest);
        tail = text[m.Length..].TrimStart('.', ' ', ':', ' ', '\t').Trim();
        return true;
    }

    /// <summary>Делит абзац на индикаторы, если несколько индикаторов записаны в одном абзаце.</summary>
    public static IEnumerable<string> SplitIndicators(string paragraph) =>
        InlineIndicatorStart().Split(paragraph).Select(Clean).Where(s => s.Length > 0);

    /// <summary>Номер семестра по символу из формы контроля или матрицы: 1–9, «A», «B», «C» — 10–12.</summary>
    public static int? SemesterOf(char ch) => ch switch
    {
        >= '1' and <= '9' => ch - '0',
        'A' or 'А' => 10,
        'B' or 'В' => 11,
        'C' or 'С' => 12,
        _ => null
    };

    /// <summary>Тип индикатора по первому слову текста: «Знать», «Умеет», «Имеет навыки», «Демонстрирует»…</summary>
    public static IndicatorType? IndicatorTypeOf(string text)
    {
        var t = text.ToLowerInvariant().Replace('ё', 'е');
        if (t.StartsWith("знать") || t.StartsWith("знает") || t.StartsWith("знания")) return IndicatorType.Knows;
        if (t.StartsWith("уметь") || t.StartsWith("умеет") || t.StartsWith("умения")) return IndicatorType.Can;
        if (t.StartsWith("владеть") || t.StartsWith("владеет") || t.StartsWith("иметь навык") ||
            t.StartsWith("имеет навык") || t.StartsWith("навык") || t.StartsWith("демонстрир") ||
            t.StartsWith("иметь опыт") || t.StartsWith("имеет опыт"))
            return IndicatorType.Owns;
        return null;
    }
}

/// <summary>Разобранный код: компетенция («ПК-1») и, если есть, индикатор («ПК-1.1.1»).</summary>
internal readonly record struct CompetenceCode(CompetenceCategory Category, string Competence, string? Indicator);
