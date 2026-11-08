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
        Clean(NamePunctuation().Replace(Abbreviation().Replace((text ?? "").ToLowerInvariant().Replace('ё', 'е'), "том числе"), " "));

    // «в т.ч.», «в т. ч.» — сокращение в названиях матриц, в плане пишут полностью (этап 5.4, B7).
    [GeneratedRegex(@"\bт\.\s*ч\.")]
    private static partial Regex Abbreviation();

    private static readonly HashSet<string> StopWords = ["и", "в", "во", "на", "по", "для", "с", "со", "к", "о", "об", "от", "из", "том", "числе"];

    /// <summary>
    /// Основы слов названия по порядку (первые 5 букв, у коротких слов — 3, латинские — целиком), без служебных слов: «Физика твёрдого
    /// тела» и «Физика твердых тел» — одно и то же. Для сопоставления строк матрицы по похожести (этап 5.4, B7).
    /// </summary>
    public static IReadOnlyList<string> NameStems(string? text) =>
        NameKey(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w))
            // Латинские слова — целиком: окончания английских слов меняют смысл («Data Scientist» и «Data Science»
            // могут быть вписаны в документы как разные названия — решение автора, этап 5.4).
            .Select(w => w.Any(char.IsAsciiLetter) ? w
                : w.Length > 5 ? w[..5] : w.Length > 3 ? w[..3] : w) // «тела» / «тел», «игры» / «игр»
            .ToList();

    /// <summary>
    /// Похожесть названий (0…1): общие основы — на число слов более длинного названия, чтобы «Электротехника и
    /// электроника» (основы «электр» у обоих слов) не совпала с «Электротехникой». Числа в названиях должны совпадать
    /// («Элективные дисциплины. 01» и «… 03» — разные).
    /// </summary>
    public static double NameSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        static IEnumerable<string> Numbers(IEnumerable<string> s) => s.Where(w => w.All(char.IsDigit));
        if (!Numbers(a).ToHashSet().SetEquals(Numbers(b))) return 0;
        // Общие основы — с повторами, как мультимножество: «проектирование … проектная» — две основы «проек».
        var common = a.GroupBy(s => s).Sum(g => Math.Min(g.Count(), b.Count(s => s == g.Key)));
        return (double)common / Math.Max(a.Count, b.Count);
    }

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
