using System.Text.RegularExpressions;

namespace RpdFos.Corpus;

/// <summary>Вид раздела настоящего ФОС и место для него в <c>fos.schema.json</c> (пустая строка — места нет).</summary>
public sealed record SectionKind(string Id, string Title, Regex Pattern, string Slot);

/// <summary>
/// Правила, по которым инструмент относит заголовки и таблицы настоящих ФОС к разделам схемы. Правила — предложение
/// для обсуждения: итог по корпусу проверяет автор. Все правила в одном месте, чтобы их было удобно править.
/// </summary>
public static class FosHeuristics
{
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    public static readonly Regex HeadingStyle = new(@"^(heading|заголовок|title|название)\b", Ic);
    private static readonly Regex Appendix = new(@"^(приложение|раздел|часть)\s*(№|n)?\s*\d+", Ic);

    /// <summary>Шапка вуза в начале каждой части ФОС и подзаголовок «по дисциплине …» — оформление, а не раздел.</summary>
    public static readonly Regex Boilerplate = new(
        @"^[«""]?(минобрнауки|федеральное\s+государственное|высшего\s+образования|московский\s+государственный|\(?фгбоу|по\s+дисциплине)", Ic);

    public static bool LooksLikeHeading(string text, bool styleHeading, bool bold, bool centered)
    {
        if (text.Length is < 3 or > 160 || !text.Any(char.IsLetter) || Boilerplate.IsMatch(text)) return false;
        if (styleHeading) return true;
        if (Appendix.IsMatch(text) && text.Length <= 120) return true;
        var letters = text.Count(char.IsLetter);
        if (letters >= 6 && text.Length <= 120 && text.Count(char.IsUpper) >= letters * 0.85) return true;
        return bold && text.Length <= 140 && !text.EndsWith(',') && !text.EndsWith(';');
    }

    /// <summary>Порядок важен: у заголовка берётся первый подошедший вид (основной), остальные считаются попутными.</summary>
    public static readonly SectionKind[] Kinds =
    [
        new("tickets", "Экзаменационные билеты", new(@"билет", Ic), "exam_tickets"),
        new("coursework", "Курсовая работа, проект", new(@"курсов\w*\s+(работ|проект)", Ic), ""),
        new("essay", "Рефераты, доклады, эссе", new(@"реферат|эссе|доклад|сообщени\w+|творческ\w+\s+задан", Ic), ""),
        new("group", "Группа заданий по разделу, семестру", new(@"^по\s+разделу|^по\s+теме|семестр", Ic), "task_bank (group, semester)"),
        new("scale", "Шкала и критерии оценивания", new(@"шкал|критери\w+\s+оцен|показател\w+\s+(и\s+)?критери|уровн\w+\s+(сформирован|освоени)|критери\w+\s+и\s+показател", Ic), "evaluation_scale"),
        new("passport", "Паспорт ФОС", new(@"паспорт", Ic), "passport"),
        new("indicators", "Компетенции и индикаторы", new(@"индикатор|этап\w*\s+формирования|перечень\s+компетенц|контролируем\w+\s+компетенц", Ic), "competence_indicators"),
        new("means", "Оценочные средства", new(@"(перечень|описание|виды|характеристика|состав|типы)\s+оценочн|оценочные\s+средства\s+(для|по|текущ|промежут)", Ic), "evaluation_means"),
        new("current", "Текущий контроль, рейтинг", new(@"текущ\w+\s+(контрол|аттестац|успеваемост)|рейтинг|модульно|контрольн\w+\s+(мероприят|точк)", Ic), "current_control"),
        new("intermediate", "Промежуточная аттестация", new(@"промежуточн|экзамен|зач[её]т", Ic), "evaluation_means / task_bank"),
        new("bank", "Задания, вопросы, тесты", new(@"вопрос|задани|задач|тест|банк|комплект|контрольн\w+\s+работ|практическ|лабораторн|коллоквиум|упражнени|типов\w+|кейс|ситуационн|вариант", Ic), "task_bank"),
        new("samples", "Образцы и примеры", new(@"образец|образцы|пример\w*\s+(оформлени|выполнени|решени)|эталон", Ic), ""),
        new("method", "Методика и порядок оценивания", new(@"методическ|порядок|процедур|методика|правила|рекомендаци", Ic), ""),
        new("approval", "Согласование и составители", new(@"составител|согласовано|протокол|утвержд|председатель|зав\w*\.?\s+кафедр|рецензент|эксперт", Ic), "approval_sheet"),
        new("title", "Титульный лист", new(@"^фонд\s+оценочных\s+средств$|^по\s+дисциплине|^министерство|^минобрнауки|^кафедра|^направление|^москва", Ic), "title_page"),
        new("appendix", "Приложение (без содержания в заголовке)", new(@"^приложение", Ic), ""),
    ];

    public static string Norm(string s) =>
        Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ").Trim();

    /// <summary>Заголовок для частот: без цифр и знаков, чтобы «Раздел 1» и «Раздел 2» считались одним.</summary>
    public static string HeadingKey(string text)
    {
        var s = Regex.Replace(Norm(text), @"[\d№\.\,\:\;\(\)«»""'\-–—_]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 70 ? s[..70] : s;
    }

    public static IEnumerable<SectionKind> MatchKinds(string headingText)
    {
        var norm = Norm(headingText);
        return Kinds.Where(k => k.Pattern.IsMatch(norm));
    }

    // ---------- Коды компетенций ----------

    private static readonly HashSet<string> NotCompetence = new(StringComparer.OrdinalIgnoreCase) { "ГОСТ", "ФГОС", "СНИП", "ОСТ", "ISO", "ТУ" };
    private static readonly Regex Code = new(@"(?<![А-Яа-яЁёA-Za-z])(?<p>[А-ЯЁ]{1,6})-(?<n>\d{1,2})(?<i>(?:\.\d{1,2}){0,2})(?!\d)", RegexOptions.Compiled);

    /// <summary>Коды вида «ПК-1» и «ПК-1.2»: префикс, номер, номера индикатора.</summary>
    public static IEnumerable<(string Full, string Prefix, bool Indicator)> Codes(string text) =>
        Code.Matches(text).Where(m => !NotCompetence.Contains(m.Groups["p"].Value))
            .Select(m => (m.Value, m.Groups["p"].Value, m.Groups["i"].Length > 0));

    // ---------- Типы оценочных средств и формы контроля ----------

    /// <summary>
    /// Слова-признаки форм контроля и оценочных средств. Значение — тип из <c>means_type</c> схемы или пустая строка,
    /// если подходящего значения в перечне нет.
    /// </summary>
    public static readonly (string Name, Regex Pattern, string EnumValue)[] MeansWords =
    [
        ("тест", new(@"\bтест\w*", Ic), "test"),
        ("устный опрос, собеседование", new(@"устн\w+\s+опрос|собеседовани|устн\w+\s+ответ", Ic), "oral_survey"),
        ("письменный опрос, контрольная работа", new(@"письменн\w+\s+(опрос|работ)|контрольн\w+\s+работ|самостоятельн\w+\s+работ", Ic), "written_survey"),
        ("практическое задание, задачи", new(@"практическ\w+\s+(задани|работ|занят)|\bзадач\w*|расч[её]тн\w+", Ic), "practical_task"),
        ("лабораторная работа, отчёт", new(@"лабораторн\w+\s+работ|отч[её]т", Ic), "lab_report"),
        ("реферат, эссе, доклад", new(@"реферат|эссе|доклад", Ic), "essay"),
        ("презентация", new(@"презентаци", Ic), "presentation"),
        ("кейс, ситуационная задача", new(@"\bкейс|ситуационн", Ic), "case_study"),
        ("проект, курсовая работа", new(@"\bпроект|курсов\w+\s+работ", Ic), "project"),
        ("вопросы к экзамену", new(@"вопрос\w*\s+(к|для)\s+экзамен|экзаменационн\w+\s+вопрос", Ic), "exam_questions"),
        ("вопросы к зачёту", new(@"вопрос\w*\s+(к|для)\s+зач[её]т|вопрос\w*\s+к\s+зач", Ic), "credit_questions"),
        ("коллоквиум", new(@"коллоквиум", Ic), ""),
        ("домашнее задание", new(@"домашн\w+\s+задани", Ic), ""),
        ("расчётно-графическая работа", new(@"расч[её]тно-графическ", Ic), ""),
        ("деловая игра, дискуссия, круглый стол", new(@"деловая\s+игр|дискусси|круглый\s+стол|мозговой", Ic), ""),
        ("портфолио", new(@"портфолио", Ic), ""),
        ("зачёт", new(@"\bзач[её]т", Ic), ""),
        ("экзамен", new(@"\bэкзамен", Ic), ""),
        ("контрольные вопросы", new(@"контрольн\w+\s+вопрос", Ic), ""),
    ];

    // ---------- Шкалы ----------

    public static readonly Regex FivePoint = new(@"отлично[\s\S]{0,400}хорошо|удовлетворительно", Ic);
    public static readonly Regex PassFail = new(@"\bне\s+зачтено|\bзачтено|\bнезачет|\bнезач[её]т", Ic);
    public static readonly Regex HundredPoint = new(@"100[\s-]*балл|\b0\s*[–-]\s*100\b|\b100\s*баллов", Ic);

    // ---------- Персональные данные ----------

    private static readonly Regex[] Names =
    [
        new(@"[А-ЯЁ][а-яё]+(?:-[А-ЯЁ][а-яё]+)?\s+[А-ЯЁ]\.\s?[А-ЯЁ]\.?", RegexOptions.Compiled),
        new(@"[А-ЯЁ]\.\s?[А-ЯЁ]\.\s?[А-ЯЁ][а-яё]+(?:-[А-ЯЁ][а-яё]+)?", RegexOptions.Compiled),
        new(@"[А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+\s+[А-ЯЁ][а-яё]+(?:вич|вна|ична|ьич|оглы|кызы)", RegexOptions.Compiled),
    ];

    /// <summary>ФИО в тексте отчёта заменяются: корпус содержит настоящие фамилии, в репозиторий они не попадают.</summary>
    public static string Mask(string text)
    {
        foreach (var rx in Names) text = rx.Replace(text, "‹ФИО›");
        return text;
    }

    /// <summary>Имя файла для отчётов: фамилия в скобках («… (Фамилия).pdf») скрывается.</summary>
    public static string MaskFileName(string name) =>
        Regex.Replace(Mask(name), @"\(([А-ЯЁ][а-яё]+)\)", "(‹ФИО›)");
}
