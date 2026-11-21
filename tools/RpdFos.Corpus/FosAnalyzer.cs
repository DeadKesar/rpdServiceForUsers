using System.Text.RegularExpressions;
using static RpdFos.Corpus.FosHeuristics;

namespace RpdFos.Corpus;

public sealed record OutlineItem(int Block, string Text, string Kind, int Size);

/// <param name="Role">passport-sections, passport-indicators, scale, rating, tickets, signatures, layout (однострочная), task (прочие).</param>
public sealed record TableInfo(int Block, int Rows, int Cols, string Role, string Header, int Formulas, int Drawings, int Merged);

/// <summary>Серия номеров заданий: подряд идущие 1, 2, 3… (набранные руками или автонумерация Word).</summary>
public sealed record NumberRun(string Source, int Start, int Length, int HeadingsInside, string Group);

public sealed class PassportInfo
{
    public string Header { get; init; } = "";
    public int Rows { get; init; }
    public int Cols { get; init; }
    public List<string> Means { get; init; } = [];       // значения последнего столбца: оценочные средства
    public bool TaskNumbers { get; init; }               // в последнем столбце есть номера заданий («вопросы 1–20», «№ 3»)
    public List<string> CompetenceCodes { get; init; } = [];
}

/// <summary>Результат разбора одного ФОС: всё, что нужно отчёту и сводке. Поля — факты, выводы делает автор.</summary>
public sealed class FosAnalysis
{
    public required FosDoc Doc { get; init; }
    public string Direction { get; set; } = "";
    public string Discipline { get; set; } = "";
    public string Year { get; set; } = "";
    public string StudyForm { get; set; } = "";
    public string Level { get; set; } = "";
    public string DocKind { get; set; } = "?";                   // «ФОС», «РПД» (рабочая программа лежит в папке ФОС) или «?» — титул не опознан
    public string? DuplicateOf { get; set; }                     // номер файла с тем же содержимым, если это точная копия
    public string Standard { get; set; } = "?";                  // «3++» — есть индикаторы достижения компетенций; «3+» — только коды компетенций
    public int Parts { get; set; }                               // частей с шапкой вуза: титул и приложения
    public List<OutlineItem> Outline { get; } = [];
    public Dictionary<string, int> KindHeadings { get; } = [];   // вид раздела → число заголовков такого вида
    public List<TableInfo> Tables { get; } = [];
    public PassportInfo? Passport { get; set; }
    public TableInfo? IndicatorsTable { get; set; }
    public bool IndicatorsHaveSkills { get; set; }               // «знать / уметь / владеть» в таблице индикаторов
    public SortedDictionary<string, int> CodePrefixes { get; } = [];     // префикс кода компетенции → число упоминаний
    public int IndicatorCodes { get; set; }
    public Dictionary<string, int> MeansWordCounts { get; } = [];
    public HashSet<string> ScaleKinds { get; } = [];
    public bool Points { get; set; }
    public bool Weeks { get; set; }
    public List<NumberRun> Runs { get; } = [];
    public int TypedItems { get; set; }
    public int AutoItems { get; set; }
    public int Tickets { get; set; }
    public Dictionary<int, int> TicketQuestions { get; } = [];   // вопросов в билете → число билетов
    public HashSet<string> TicketHeader { get; } = [];
    public int OptionLines { get; set; }                         // «а) … б) …» — варианты ответов теста
    public int AnswerLines { get; set; }                         // «Ответ: …», эталоны
    public int ScoreMentions { get; set; }                       // «(2 балла)» в заданиях
    public int IndicatorRefsInText { get; set; }                 // коды индикаторов в тексте вне таблиц
    public int SemesterHeadings { get; set; }
    public int LmsQuestions { get; set; }                        // «Вопрос 1 (МВ) …» — тесты в формате системы тестирования
    public HashSet<string> LmsTypes { get; } = [];
    public int LmsAnswers { get; set; }                          // «Ответ 2 (100) …» — ответ с весом
    public int Formulas { get; set; }
    public int FormulaTables { get; set; }                       // таблиц, в которых есть формулы
    public int Drawings { get; set; }                            // крупные рисунки; эмблемы в шапках не входят
    public int SmallDrawings { get; set; }
    public List<(FosImage Image, string Where)> Images { get; } = [];   // рисунки вне шапок любого размера и раздел, где стоят
    public int PageScans { get; set; }                           // страниц PDF, занятых картинкой целиком (скан), — это не рисунки заданий
    public int MathSymbolParagraphs { get; set; }
    public int TaskTables { get; set; }
    public int MergedTables { get; set; }                        // таблиц с объединёнными ячейками (без оформительских и подписей)
    public int Authors { get; set; }                             // абзацев с ФИО (замаскированы в отчёте)
    public List<(string Key, string Detail)> NoSlot { get; } = [];   // что не ложится в схему: устойчивый ключ для частот и пояснение
}

public static class FosAnalyzer
{
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex DirectionRx = new(@"\b\d{2}\.\d{2}\.\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"\b(20\d\d)\b", RegexOptions.Compiled);
    private static readonly Regex DisciplineRx = new(@"по\s+дисциплине\s*:?\s*(?<d>.+)$", Ic);
    private static readonly Regex FormRx = new(@"очно-заочн\w+|заочн\w+|очн\w+", Ic);
    private static readonly Regex LevelRx = new(@"бакалавр\w*|магистр\w*|специалист\w*", Ic);
    // Номер задания, набранный руками: «Вопрос 3», «Задание № 5:», «12.», «7)». Дробные «1.2» — номера разделов, не заданий.
    private static readonly Regex LabeledNumber = new(@"^\s*(?<label>Вопрос|Задание|Задача|Тест|Билет|Вариант|Упражнение)\s*(?:№|N)?\s*(?<n>\d{1,3})(?![\d])(?!\.\d)", Ic);
    private static readonly Regex PlainNumber = new(@"^\s*(?<n>\d{1,3})\s*(?:[.)]|:|\s[—–-]\s)(?!\d)", Ic);
    private static readonly Regex LmsQuestion = new(@"^\s*Вопрос\s+\d+\s*\((?<t>[А-ЯA-Z]{1,3})\)", Ic);
    private static readonly Regex LmsAnswer = new(@"^\s*Ответ(\s+\d+)?\s*\(\s*\d+\s*\)", Ic);
    private static readonly Regex TicketRx = new(@"^(экзаменационный\s+)?билет\s*(№|n)?\s*(?<n>\d+)", Ic);
    private static readonly Regex TicketQuestion = new(@"(?:^|\s)(?<n>[1-6])\s*[.)]\s*\S", RegexOptions.Compiled);
    private static readonly Regex OptionRx = new(@"^\s*[а-дa-e]\s*\)\s+\S", Ic);
    private static readonly Regex AnswerRx = new(@"^\s*(правильн\w+\s+|верн\w+\s+)?(ответ|эталон)\w*\s*[:.]|ключ\s+к", Ic);
    private static readonly Regex ScoreRx = new(@"\(\s*\d+([.,]\d+)?\s*(балл|б\.)|\b\d+([.,]\d+)?\s*балл", Ic);
    private static readonly Regex MathSymbols = new(@"[∑∫√≤≥≠≈∈∉⊂⊃⊆⊇∪∩∀∃→⇒⇔≡∞∂∇∧∨¬⊕]", RegexOptions.Compiled);
    private static readonly Regex Semester = new(@"семестр", Ic);
    private static readonly Regex GroupWord = new(@"^(раздел|тема|модуль|вариант|уровень|часть|блок|группа)\b", Ic);

    public static FosAnalysis Analyze(FosDoc doc)
    {
        var a = new FosAnalysis { Doc = doc };
        if (doc.Error is not null) return a;

        var paragraphs = doc.Paragraphs.ToList();
        a.DocKind = KindOf(paragraphs);
        var head = paragraphs.Take(80).Select(p => p.Text).ToList();
        a.Direction = head.Select(t => DirectionRx.Match(t)).FirstOrDefault(m => m.Success)?.Value
                      ?? DirectionRx.Match(doc.FileName).Value;
        a.Year = head.Select(t => YearRx.Match(t)).FirstOrDefault(m => m.Success)?.Value ?? "";
        a.StudyForm = head.Select(t => FormRx.Match(t)).FirstOrDefault(m => m.Success)?.Value.ToLowerInvariant() ?? "";
        a.Level = head.Select(t => LevelRx.Match(t)).FirstOrDefault(m => m.Success)?.Value.ToLowerInvariant() ?? "";
        a.Discipline = Mask(FindDiscipline(paragraphs));

        FillOutline(a, paragraphs);
        FillTables(a);
        FillCodes(a);
        FillText(a, paragraphs);
        FillNumbering(a, paragraphs);
        FillTickets(a, paragraphs);
        FillNoSlot(a);
        return a;
    }

    /// <summary>
    /// ФОС или рабочая программа: в папку ФОС попадают и РПД (пара к ФОС), их нельзя считать в статистике ФОС.
    /// Решает то, что стоит в начале раньше: «ФОНД ОЦЕНОЧНЫХ СРЕДСТВ» или «РАБОЧАЯ ПРОГРАММА».
    /// </summary>
    private static string KindOf(List<FosParagraph> paragraphs)
    {
        foreach (var p in paragraphs.Take(60))
        {
            var text = Norm(p.Text);
            if (Regex.IsMatch(text, @"^рабочая\s+программа")) return "РПД";
            if (Regex.IsMatch(text, @"^фонд\w*\s+оценочн")) return "ФОС";
        }
        return "?";
    }

    private static string FindDiscipline(List<FosParagraph> paragraphs)
    {
        var head = paragraphs.Take(60).ToList();
        foreach (var p in head)
            if (DisciplineRx.Match(p.Text) is { Success: true } m && m.Groups["d"].Value.Length > 2)
                return Trim(m.Groups["d"].Value.Trim('«', '»', '"', ' ', '.'), 90);
        // Название дисциплины часто стоит отдельной строкой после «ФОНД ОЦЕНОЧНЫХ СРЕДСТВ».
        var i = head.FindIndex(p => Regex.IsMatch(p.Text, @"^фонд\s+оценочных\s+средств\s*$", Ic));
        if (i < 0) return "";
        var next = head.Skip(i + 1).Select(p => p.Text).FirstOrDefault(t => t.Length > 3);
        return next is null ? "" : Trim(next.Trim('«', '»', '"', ' '), 90);
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    // ---------- Структура ----------

    private static void FillOutline(FosAnalysis a, List<FosParagraph> paragraphs)
    {
        var headings = paragraphs.Where(p => p.Heading).ToList();
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            var kinds = MatchKinds(h.Text).ToList();
            var kind = kinds.Count > 0 ? kinds[0].Id : "other";
            foreach (var k in kinds.Select(k => k.Id).DefaultIfEmpty("other").Distinct())
                a.KindHeadings[k] = a.KindHeadings.GetValueOrDefault(k) + 1;
            var next = i + 1 < headings.Count ? headings[i + 1].Index : a.Doc.Blocks.Count;
            a.Outline.Add(new OutlineItem(h.Index, Mask(Trim(h.Text, 90)), kind, next - h.Index - 1));
            if (Semester.IsMatch(h.Text)) a.SemesterHeadings++;
        }
    }

    // ---------- Таблицы ----------

    private static void FillTables(FosAnalysis a)
    {
        foreach (var t in a.Doc.Tables)
        {
            var role = TableRole(t);
            var header = string.Join(" | ", t.Rows.FirstOrDefault()?.Select(c => Trim(Mask(c), 28)) ?? []);
            a.Tables.Add(new TableInfo(t.Index, t.Rows.Count, t.Cols, role, Trim(header, 130), t.Formulas, t.Drawings, t.Merged));
            if (t.Formulas > 0) a.FormulaTables++;
            if (role == "task") a.TaskTables++;
            if (t.Merged > 0 && role is not ("layout" or "signatures")) a.MergedTables++;
        }
        // Шапка вуза с эмблемой в таблице (F35): такая таблица открывает часть ФОС так же, как строка «МИНОБРНАУКИ РОССИИ».
        a.Parts += a.Doc.Tables.Count(t => t.SmallDrawings > 0 && t.Rows.Count <= 3 && t.Cols <= 1);

        var passport = a.Doc.Tables.FirstOrDefault(t => TableRole(t) == "passport-sections");
        if (passport is not null) a.Passport = ReadPassport(passport);
        var indicators = a.Doc.Tables.FirstOrDefault(t => TableRole(t) == "passport-indicators");
        if (indicators is not null)
        {
            a.IndicatorsTable = a.Tables.First(x => x.Block == indicators.Index);
            // Отдельный столбец результатов обучения: слова «знать / уметь / владеть» в тексте индикатора (УК-7.1. Знать …) — не он.
            var header = Norm(string.Join(" ", indicators.Rows.FirstOrDefault() ?? []));
            a.IndicatorsHaveSkills = Regex.IsMatch(header, @"результат|знани|умени|навык");
        }
    }

    private static string TableRole(FosTable t)
    {
        var head = Norm(string.Join(" ", t.Rows.Take(2).SelectMany(r => r)));
        var all = Norm(string.Join(" ", t.Rows.SelectMany(r => r)));
        // Одна строка или один столбец из нескольких строк — оформление (шапка с эмблемой, рамка, подпись), не данные.
        if ((t.Rows.Count <= 1 && t.Cols <= 3) || (t.Cols <= 1 && t.Rows.Count <= 3)) return "layout";
        if (head.Contains("контролируем") && head.Contains("компетенц")) return "passport-sections";
        if (head.Contains("индикатор") && head.Contains("компетенц")) return "passport-indicators";
        if (Regex.IsMatch(head, @"подпис|ф\.\s?и\.\s?о") && t.Rows.Count <= 6) return "signatures";
        if (head.Contains("билет") || t.Rows.SelectMany(r => r).Any(c => TicketRx.IsMatch(Norm(c)))) return "tickets";
        if (Regex.IsMatch(head, @"критери|шкал|оценк|уровень|показател")) return "scale";
        if (Regex.IsMatch(head, @"недел|модул|рейтинг|балл")) return "rating";
        return all.Length == 0 ? "layout" : "task";
    }

    private static PassportInfo ReadPassport(FosTable t)
    {
        // Шапка — строки до первой строки данных: в первом столбце номер, в остальных есть слова. Строка «1 2 3 4 5»
        // с номерами столбцов — ещё шапка.
        var firstData = t.Rows.ToList().FindIndex(r => r.Count > 1 && Regex.IsMatch(r[0].Trim(), @"^\d+\.?$") &&
                                                       r.Skip(1).Any(c => c.Count(char.IsLetter) >= 3));
        if (firstData < 0) firstData = Math.Min(1, t.Rows.Count);
        var data = t.Rows.Skip(firstData).Where(r => r.Count > 1).ToList();
        var means = data.Select(r => r[^1]).Where(c => c.Length > 0).ToList();
        var values = means.SelectMany(m => Regex.Split(m, @"[;\n]|,\s+(?=[А-ЯЁ])"))
            .Select(v => Norm(Regex.Replace(v, @"[\d№]+[\d\s,.\-–—]*", " ")).Trim(' ', '.', ',', ':'))
            .Where(v => v.Length > 1).Distinct().ToList();
        var numbers = means.Any(m => Regex.IsMatch(m, @"(вопрос|задани|задач|тест|вариант|№)\w*\s*[\d№]|\d+\s*[–—-]\s*\d+", Ic));
        var codes = t.Rows.SelectMany(r => r).SelectMany(c => Codes(c)).Select(c => c.Full).Distinct().ToList();
        return new PassportInfo
        {
            // Объединённые ячейки повторяются в строках шапки — одинаковые подряд идущие значения схлопываются.
            Header = Trim(string.Join(" | ", t.Rows.Take(firstData > 0 ? firstData : 1).SelectMany(r => r).Select(c => Trim(c, 40))
                .Aggregate(new List<string>(), (l, c) => { if (l.Count == 0 || l[^1] != c) l.Add(c); return l; })), 200),
            Rows = data.Count, Cols = t.Cols, Means = values, TaskNumbers = numbers, CompetenceCodes = codes
        };
    }

    // ---------- Коды компетенций ----------

    private static void FillCodes(FosAnalysis a)
    {
        var texts = a.Doc.Paragraphs.Select(p => p.Text).Concat(a.Doc.Tables.SelectMany(t => t.Rows.SelectMany(r => r)));
        var indicators = new HashSet<string>();
        foreach (var (full, prefix, indicator) in texts.SelectMany(t => Codes(t)))
        {
            a.CodePrefixes[prefix] = a.CodePrefixes.GetValueOrDefault(prefix) + 1;
            if (indicator) indicators.Add(full);
        }
        a.IndicatorCodes = indicators.Count;
        // Поколение ФГОС по составу кодов: индикаторы достижения компетенций (ПК-1.2) есть только в ФГОС 3++.
        a.Standard = indicators.Count > 0 ? "3++" : a.CodePrefixes.Count > 0 ? "3+" : "?";
    }

    // ---------- Слова-признаки по всему тексту ----------

    private static void FillText(FosAnalysis a, List<FosParagraph> paragraphs)
    {
        var tableCells = a.Doc.Tables.SelectMany(t => t.Rows.SelectMany(r => r)).ToList();
        var all = Norm(string.Join("\n", paragraphs.Select(p => p.Text).Concat(tableCells)));
        foreach (var (name, pattern, _) in MeansWords)
        {
            var n = pattern.Matches(all).Count;
            if (n > 0) a.MeansWordCounts[name] = n;
        }
        if (FivePoint.IsMatch(all)) a.ScaleKinds.Add("five_point");
        if (PassFail.IsMatch(all)) a.ScaleKinds.Add("pass_fail");
        if (HundredPoint.IsMatch(all)) a.ScaleKinds.Add("hundred_point");
        a.Points = Regex.IsMatch(all, @"\bбалл");
        a.Weeks = Regex.IsMatch(all, @"недел");
        a.Authors = paragraphs.Count(p => Mask(p.Text) != p.Text);
        a.Formulas = paragraphs.Sum(p => p.Formulas) + a.Doc.Tables.Sum(t => t.Formulas);
        var headers = paragraphs.Where(p => Regex.IsMatch(p.Text, @"^минобрнауки", Ic)).Select(p => p.Index).ToList();
        a.Parts += headers.Count;
        // Эмблема вуза — оформление, не рисунок задания, хотя на титуле она крупнее 4,5 см: стоит в первых блоках, рядом со
        // строкой «МИНОБРНАУКИ РОССИИ», в пустом абзаце прямо перед заголовком или в одностолбцовой таблице-шапке.
        bool IsEmblem(FosParagraph p) =>
            p.Index <= 3 || headers.Any(h => p.Index >= h - 3 && p.Index <= h + 8) ||
            (p.Text.Length <= 3 && paragraphs.Any(h => h.Heading && h.Index > p.Index && h.Index <= p.Index + 4));
        var emblems = paragraphs.Where(p => p.Drawings > 0 && IsEmblem(p)).Sum(p => p.Drawings)
                      + a.Doc.Tables.Where(t => t.Drawings > 0 && t.Rows.Count <= 3 && t.Cols <= 1).Sum(t => t.Drawings);
        a.Drawings = a.Doc.Images - emblems;
        a.SmallDrawings = a.Doc.SmallImages + emblems;

        // Рисунки вне шапок — каждый с ближайшим заголовком выше: по нему видно, в каких приложениях они стоят.
        string Where(int block)
        {
            var h = paragraphs.LastOrDefault(x => x.Heading && x.Index < block);
            return h is null ? "до первого заголовка" : MatchKinds(h.Text).FirstOrDefault()?.Title ?? "заголовок без вида";
        }
        foreach (var b in a.Doc.Blocks)
        {
            var images = b is FosParagraph fp ? fp.Images : ((FosTable)b).Images;
            var emblem = b is FosParagraph para ? IsEmblem(para) : ((FosTable)b) is { Rows.Count: <= 3, Cols: <= 1 };
            if (images.Count > 0 && !emblem)
                foreach (var image in images) a.Images.Add((image, Where(b.Index)));
        }
        // В PDF «картинки» — ещё и мелкие обрывки страницы (линии, значки): рисунками заданий считаются только крупные.
        foreach (var image in a.Doc.PageImages.Where(i => !i.PageScan && i.Big)) a.Images.Add((image, "страницы PDF"));
        a.PageScans = a.Doc.PageImages.Count(i => i.PageScan);
        foreach (var q in paragraphs.Select(p => LmsQuestion.Match(p.Text)).Where(m => m.Success))
        {
            a.LmsQuestions++;
            a.LmsTypes.Add(q.Groups["t"].Value.ToUpperInvariant());
        }
        a.LmsAnswers = paragraphs.Count(p => LmsAnswer.IsMatch(p.Text));
        a.MathSymbolParagraphs = paragraphs.Count(p => MathSymbols.IsMatch(p.Text));
        a.OptionLines = paragraphs.Count(p => OptionRx.IsMatch(p.Text));
        a.AnswerLines = paragraphs.Count(p => AnswerRx.IsMatch(p.Text));
        a.ScoreMentions = paragraphs.Count(p => ScoreRx.IsMatch(p.Text));
        a.IndicatorRefsInText = paragraphs.Count(p => Codes(p.Text).Any(c => c.Indicator));
    }

    // ---------- Нумерация заданий ----------

    private static int? TypedNumberOf(string text)
    {
        var m = LabeledNumber.Match(text);
        if (!m.Success) m = PlainNumber.Match(text);
        return m.Success && int.TryParse(m.Groups["n"].Value, out var n) ? n : null;
    }

    private static void FillNumbering(FosAnalysis a, List<FosParagraph> paragraphs)
    {
        var items = new List<(int Block, int Number, string Source, int? NumId)>();
        var counters = new Dictionary<int, int>();
        foreach (var p in paragraphs.Where(p => !p.Heading))
        {
            if (p.NumId is { } nid && p.NumLevel == 0 && p.NumFormat is not ("bullet" or "none"))
            {
                counters[nid] = counters.GetValueOrDefault(nid) + 1;
                items.Add((p.Index, counters[nid], "авто", nid));
                a.AutoItems++;
            }
            else if (TypedNumberOf(p.Text) is { } n)
            {
                items.Add((p.Index, n, "вручную", null));
                a.TypedItems++;
            }
        }

        var headings = paragraphs.Where(p => p.Heading).ToList();
        for (var i = 0; i < items.Count;)
        {
            var j = i;
            while (j + 1 < items.Count && items[j + 1].Source == items[i].Source && items[j + 1].NumId == items[i].NumId &&
                   items[j + 1].Number == items[j].Number + 1) j++;
            var from = items[i].Block;
            var to = items[j].Block;
            var inside = headings.Count(h => h.Index > from && h.Index < to);
            var group = headings.LastOrDefault(h => h.Index < from)?.Text ?? "";
            a.Runs.Add(new NumberRun(items[i].Source, items[i].Number, j - i + 1, inside, Mask(Trim(group, 60))));
            i = j + 1;
        }
    }

    // ---------- Билеты ----------

    private static void FillTickets(FosAnalysis a, List<FosParagraph> paragraphs)
    {
        var regions = new List<string>();
        var headers = paragraphs.Where(p => TicketRx.IsMatch(Norm(p.Text))).ToList();
        for (var i = 0; i < headers.Count; i++)
        {
            var from = headers[i].Index;
            var to = i + 1 < headers.Count ? headers[i + 1].Index : Math.Min(a.Doc.Blocks.Count, from + 12);
            // Билет не длиннее 12 блоков: после последнего билета дальше идёт остальной документ.
            to = Math.Min(to, from + 12);
            regions.Add(string.Join(" ", a.Doc.Blocks.Where(b => b.Index >= from && b.Index < to).Select(b =>
                b is FosParagraph p ? p.Text : string.Join(" ", ((FosTable)b).Rows.SelectMany(r => r)))));
        }
        // Билеты таблицами: билет — строка или ячейка с заголовком «Билет № N».
        var tableTickets = new List<string>();
        foreach (var t in a.Doc.Tables)
            foreach (var row in t.Rows.Where(r => r.Any(c => TicketRx.IsMatch(Norm(c)))))
                tableTickets.Add(string.Join(" ", row));
        if (tableTickets.Count > regions.Count) regions = tableTickets;

        a.Tickets = regions.Count;
        foreach (var region in regions)
        {
            var q = TicketQuestion.Matches(region).Select(m => m.Groups["n"].Value).Distinct().Count();
            a.TicketQuestions[q] = a.TicketQuestions.GetValueOrDefault(q) + 1;
        }

        if (headers.Count > 0)
        {
            var from = Math.Max(0, headers[0].Index - 14);
            var window = Norm(string.Join(" ", paragraphs.Where(p => p.Index >= from && p.Index <= headers[0].Index + 14).Select(p => p.Text)));
            foreach (var (word, mark) in new[] { ("кафедр", "кафедра"), ("дисциплин", "дисциплина"), ("курс", "курс"), ("направлен", "направление"),
                         ("зав", "зав. кафедрой"), ("учебн", "учебный год"), ("утвержд", "«утверждаю»"), ("протокол", "протокол") })
                if (window.Contains(word)) a.TicketHeader.Add(mark);
        }
    }

    // ---------- Что не ложится в схему ----------

    private static void FillNoSlot(FosAnalysis a)
    {
        foreach (var k in Kinds.Where(k => k.Slot.Length == 0 && k.Id != "appendix"))
            if (a.KindHeadings.TryGetValue(k.Id, out var n))
                a.NoSlot.Add(($"раздел «{k.Title}»", $"{n} заголовк."));

        var foreign = a.CodePrefixes.Where(c => c.Key is not ("УК" or "ОПК" or "ПК")).ToList();
        if (foreign.Count > 0)
            a.NoSlot.Add(("коды компетенций с префиксом вне шаблона схемы (УК|ОПК|ПК)", string.Join(", ", foreign.Select(c => $"{c.Key}×{c.Value}"))));
        if (a.Passport is { TaskNumbers: false, Means.Count: > 0 })
            a.NoSlot.Add(("паспорт ссылается на оценочные средства названием, без номеров заданий", "пара «группа, номер» не заполняется"));
        if (a.IndicatorsHaveSkills)
            a.NoSlot.Add(("в таблице индикаторов есть отдельный столбец результатов обучения", "в схеме у индикатора только код и текст"));
        if (a.MergedTables > 0)
            a.NoSlot.Add(("таблицы с объединёнными ячейками", $"{a.MergedTables}; шаг B4 решает, поддерживаются ли объединённые ячейки"));
        if (a.OptionLines >= 8)
            a.NoSlot.Add(("тестовые задания с вариантами ответа", $"строк «а) …»: {a.OptionLines}; в схеме вариантов нет, только текст задания"));
        if (a.LmsQuestions > 0)
            a.NoSlot.Add(("тесты в формате системы тестирования: вопрос с типом, ответы с весами",
                $"вопросов {a.LmsQuestions}, типы {string.Join("/", a.LmsTypes)}, ответов с весом {a.LmsAnswers}"));
        if (a.Formulas > 0)
            a.NoSlot.Add(("формулы (не линейная запись)", $"{a.Formulas}; FR-DOC-20 допускает только линейную запись"));
        if (a.Images.Count > 0)
            a.NoSlot.Add(("рисунки в заданиях (вне шапок)", $"{a.Images.Count}; нужна вставка изображений в текст (решение 10 пересмотрено автором 21.11.2026)"));
        if (a.TaskTables > 0)
            a.NoSlot.Add(("таблицы внутри приложений", $"{a.TaskTables}; нужна поддержка таблиц в форматированном тексте (шаг B4)"));
        if (a.Tickets > 0 && a.TicketQuestions.Keys.Any(q => q is < 2 or > 3))
            a.NoSlot.Add(("билеты не с двумя-тремя вопросами", string.Join(", ", a.TicketQuestions.Where(q => q.Key is < 2 or > 3).Select(q => $"{q.Key} вопр. × {q.Value}"))));
        if (a.Runs.Any(r => r.Length >= 3 && r.HeadingsInside > 0))
            a.NoSlot.Add(("нумерация заданий сквозная через заголовки групп", "в схеме нумерация внутри группы"));
    }
}
