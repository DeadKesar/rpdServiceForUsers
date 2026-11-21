using System.Text;
using System.Text.RegularExpressions;
using static RpdFos.Corpus.FosHeuristics;

namespace RpdFos.Corpus;

/// <summary>
/// Отчёты режима <c>--fos</c>: по файлу, сводка, таблицы для Excel и карта файла оформления. Имена файлов и ФИО
/// в отчётах скрыты (<see cref="FosHeuristics.MaskFileName"/>): отчёты читает не только автор.
/// </summary>
public static class FosReports
{
    public const string Folder = "ФОС";

    public static void Write(string output, IReadOnlyList<FosAnalysis> items, FosAnalysis? template,
        TimeSpan elapsed, DateTimeOffset started)
    {
        // Режим сам отвечает за свою папку; остальные отчёты прогона он не трогает.
        var dir = Path.Combine(output, Folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        foreach (var a in items) File.WriteAllText(Path.Combine(dir, a.Doc.Id + ".md"), File_(a));
        File.WriteAllText(Path.Combine(dir, "Сводка.md"), Summary(items, template, elapsed, started));
        File.WriteAllText(Path.Combine(dir, "Файлы.csv"), FilesCsv(items), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "Заголовки.csv"), HeadingsCsv(items), new UTF8Encoding(true));
        if (template is not null) File.WriteAllText(Path.Combine(dir, "Шаблон.md"), Template(template, items));
    }

    // ---------- По файлу ----------

    private static string File_(FosAnalysis a)
    {
        var d = a.Doc;
        var s = new StringBuilder();
        s.AppendLine($"# {d.Id} — {Md(MaskFileName(d.FileName))}");
        s.AppendLine();
        if (d.Error == MissingMark)
        {
            s.AppendLine("Файла нет в папке корпуса. Номер закреплён за ним в реестре и повторно не выдаётся.");
            return s.ToString();
        }
        if (d.Error is not null)
        {
            s.AppendLine($"Файл не разобран: {d.Error}");
            return s.ToString();
        }
        if (a.DocKind == "РПД") s.AppendLine("**Это рабочая программа, не ФОС** (лежит в папке ФОС): в статистику ФОС не входит.\n");
        if (a.DuplicateOf is not null) s.AppendLine($"**Точная копия {a.DuplicateOf}**: содержимое файла совпадает, в статистику не входит.\n");
        s.AppendLine($"{d.Format} ({d.Source}) · страниц {d.Pages?.ToString() ?? "?"} · абзацев {d.Paragraphs.Count()} · таблиц {d.Tables.Count()} · " +
                     $"формул {a.Formulas} · крупных рисунков {a.Drawings} (мелких {a.SmallDrawings}) · знаков текста {d.TextChars}");
        s.AppendLine($"Направление {Or(a.Direction)} · {Or(a.Level)} · {Or(a.StudyForm)} · {Or(a.Year)} · ФГОС {a.Standard} · частей с шапкой вуза {a.Parts} · дисциплина: {Or(a.Discipline)}");
        if (d.TextChars < 300) s.AppendLine("\n**Текста почти нет** (скан или изображение?): разбирать нечего.");

        s.AppendLine("\n## Разделы по заголовкам");
        s.AppendLine($"Заголовков {a.Outline.Count}; по видам: " +
                     string.Join(", ", a.KindHeadings.OrderByDescending(k => k.Value).Select(k => $"{KindTitle(k.Key)} {k.Value}")));
        s.AppendLine("\n| Блок | Заголовок | Вид | Блоков до следующего |\n|---|---|---|---|");
        foreach (var o in a.Outline.Take(45)) s.AppendLine($"| {o.Block} | {Md(o.Text)} | {o.Kind} | {o.Size} |");
        if (a.Outline.Count > 45) s.AppendLine($"| … | ещё {a.Outline.Count - 45} заголовков | | |");

        s.AppendLine("\n## Паспорт и индикаторы");
        if (a.Passport is { } p)
        {
            s.AppendLine($"Таблица паспорта: {p.Rows} строк данных × {p.Cols} столбцов. Шапка: {Md(p.Header)}");
            s.AppendLine($"Оценочные средства в последнем столбце: {(p.Means.Count > 0 ? Md(Trim(string.Join("; ", p.Means), 300)) : "нет")}; номера заданий: {(p.TaskNumbers ? "есть" : "нет")}.");
            s.AppendLine($"Коды компетенций в таблице: {(p.CompetenceCodes.Count > 0 ? string.Join(", ", p.CompetenceCodes.Take(20)) : "нет")}.");
        }
        else s.AppendLine("Таблицы паспорта (разделы × компетенции × оценочные средства) не найдено.");
        s.AppendLine(a.IndicatorsTable is { } it
            ? $"Таблица индикаторов: {it.Rows}×{it.Cols}; «знать / уметь / владеть»: {(a.IndicatorsHaveSkills ? "есть" : "нет")}. Шапка: {Md(it.Header)}"
            : "Таблицы индикаторов (компетенция × индикатор) не найдено.");
        s.AppendLine($"Префиксы кодов: {(a.CodePrefixes.Count > 0 ? string.Join(", ", a.CodePrefixes.Select(c => $"{c.Key}×{c.Value}")) : "нет")}; разных кодов индикаторов: {a.IndicatorCodes}.");

        s.AppendLine("\n## Оценочные средства, текущий контроль, шкалы");
        s.AppendLine("Слова-признаки: " + (a.MeansWordCounts.Count > 0 ? string.Join(", ", a.MeansWordCounts.OrderByDescending(m => m.Value).Select(m => $"{m.Key} {m.Value}")) : "нет"));
        s.AppendLine($"Шкалы: {(a.ScaleKinds.Count > 0 ? string.Join(", ", a.ScaleKinds) : "не найдено")}; баллы: {YesNo(a.Points)}; недели: {YesNo(a.Weeks)}.");

        s.AppendLine("\n## Банк заданий и нумерация");
        var series = a.Runs.Where(r => r.Length >= 3).ToList();
        s.AppendLine($"Нумерованных пунктов: вручную {a.TypedItems}, автонумерация {a.AutoItems}. Серий из 3+ подряд: {series.Count} ({NumberingKind(a)}).");
        foreach (var r in series.Take(14))
            s.AppendLine($"- {r.Source}: с {r.Start}, {r.Length} шт., заголовков внутри серии {r.HeadingsInside}; группа: {Md(Or(r.Group))}");
        if (series.Count > 14) s.AppendLine($"- … ещё {series.Count - 14} серий");
        s.AppendLine($"Варианты ответа теста (а, б, в…): {a.OptionLines} строк; строк «ответ / эталон»: {a.AnswerLines}; задания с баллами: {a.ScoreMentions}; " +
                     $"коды индикаторов в тексте заданий: {a.IndicatorRefsInText}; заголовков с «семестр»: {a.SemesterHeadings}.");
        if (a.LmsQuestions > 0)
            s.AppendLine($"Тесты в формате системы тестирования («Вопрос 1 (МВ)», «Ответ 2 (100)»): вопросов {a.LmsQuestions}, типы {string.Join(", ", a.LmsTypes)}, ответов с весом {a.LmsAnswers}.");

        s.AppendLine("\n## Билеты");
        if (a.Tickets > 0)
            s.AppendLine($"Билетов: {a.Tickets}; вопросов в билете: {string.Join(", ", a.TicketQuestions.OrderBy(q => q.Key).Select(q => $"{q.Key}×{q.Value}"))}; " +
                         $"шапка рядом с первым билетом: {(a.TicketHeader.Count > 0 ? string.Join(", ", a.TicketHeader) : "не опознана")}.");
        else s.AppendLine("Билетов не найдено.");

        s.AppendLine("\n## Таблицы, формулы, рисунки");
        var tables = a.Tables.Where(t => t.Role != "layout").ToList();
        s.AppendLine($"Таблиц {a.Tables.Count}, из них оформительских (1 строка) {a.Tables.Count(t => t.Role == "layout")}; с формулами {a.FormulaTables}; " +
                     $"абзацев с символами формул (∑, ≤, ∈ …) {a.MathSymbolParagraphs}.");
        foreach (var t in tables.Take(25))
            s.AppendLine($"- блок {t.Block}: {t.Role}, {t.Rows}×{t.Cols}{(t.Merged > 0 ? $", объединённых ячеек {t.Merged}" : "")}{(t.Formulas > 0 ? $", формул {t.Formulas}" : "")}{(t.Drawings > 0 ? $", рисунков {t.Drawings}" : "")}; шапка: {Md(t.Header)}");
        if (tables.Count > 25) s.AppendLine($"- … ещё {tables.Count - 25} таблиц");

        s.AppendLine("\n## Не ложится в схему");
        if (a.NoSlot.Count == 0) s.AppendLine("Ничего не найдено правилами инструмента.");
        foreach (var (key, detail) in a.NoSlot) s.AppendLine($"- {key}: {Md(detail)}");
        return s.ToString();
    }

    // ---------- Сводка ----------

    private static string Summary(IReadOnlyList<FosAnalysis> items, FosAnalysis? template, TimeSpan elapsed, DateTimeOffset started)
    {
        var ok = items.Where(a => a.Doc.Error is null).ToList();
        var text = ok.Where(InStatistics).ToList();
        // «N (3++: M)»: ФГОС 3++ в корпусе мало, а файлов одной дисциплины много — число без разбивки вводит в заблуждение.
        string Cnt(Func<FosAnalysis, bool> pred)
        {
            var n = text.Count(pred);
            var n3 = text.Count(a => a.Standard == "3++" && pred(a));
            return n3 > 0 ? $"{n} (3++: {n3})" : n.ToString();
        }
        static string Ids(IEnumerable<FosAnalysis> g) => string.Join(", ", g.Take(7).Select(a => a.Doc.Id)) + (g.Count() > 7 ? ", …" : "");

        var s = new StringBuilder();
        s.AppendLine("# Сводка по настоящим ФОС (режим --fos)");
        s.AppendLine();
        s.AppendLine($"Запуск {started:yyyy-MM-dd HH:mm}, {elapsed.TotalSeconds:0} с. Номеров {items.Count}, файлов в папке {items.Count(a => a.Doc.Error != MissingMark)}, разобрано {ok.Count}. " +
                     $"В статистике {text.Count} ФОС: не входят рабочие программы ({ok.Count(a => a.DocKind == "РПД")}), точные копии ({ok.Count(a => a.DuplicateOf is not null)}) и файлы без текста ({ok.Count(a => a.Doc.TextChars < 300)}). " +
                     "Правила отнесения к разделам — предложение инструмента; спорное названо ниже. В скобках «3++» — сколько из этих файлов по ФГОС 3++.");

        s.AppendLine("\n## 1. Корпус");
        s.AppendLine($"\nПо ФГОС 3++ (есть индикаторы достижения компетенций): {text.Count(a => a.Standard == "3++")} ({Ids(text.Where(a => a.Standard == "3++"))}); " +
                     $"по ФГОС 3+ (только коды ОК, ОПК, ПК, ПСК): {text.Count(a => a.Standard == "3+")}; не определено: {text.Count(a => a.Standard == "?")}. " +
                     "Стандарт определён по составу кодов, не по тексту титула.");
        s.AppendLine("\n| Дисциплина | Файлов | ФГОС | Формат | ID |\n|---|---|---|---|---|");
        foreach (var g in items.GroupBy(Family).OrderByDescending(g => g.Count()))
            s.AppendLine($"| {Md(g.Key)} | {g.Count()} | {string.Join(", ", g.Select(a => a.Standard).Distinct())} | {string.Join(", ", g.Select(a => a.Doc.Format).Distinct())} | {Ids(g)} |");
        s.AppendLine("\n| ID | Вид | Формат | Стр. | Абз. | Табл. | Направление | Год | ФГОС | Частей | Заметка |\n|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var a in items)
            s.AppendLine($"| [{a.Doc.Id}]({a.Doc.Id}.md) | {a.DocKind} | {a.Doc.Format} | {a.Doc.Pages} | {a.Doc.Paragraphs.Count()} | {a.Doc.Tables.Count()} | {Or(a.Direction)} | {Or(a.Year)} | " +
                         $"{a.Standard} | {a.Parts} | {Md(NoteOf(a))} |");

        s.AppendLine("\n## 2. Разделы настоящих ФОС и место в схеме");
        s.AppendLine($"\nФайлов с заголовком такого вида (из {text.Count}). Столбец «Файл оформления» — есть ли заголовок такого вида в `FOS_TemplateBookmarks.docx`. Слот «нет» — места в схеме нет.\n");
        s.AppendLine("| Вид раздела | Слот схемы | Файлов | Файл оформления |\n|---|---|---|---|");
        var templateKinds = template?.KindHeadings.Keys.ToHashSet() ?? [];
        foreach (var k in Kinds)
            s.AppendLine($"| {k.Title} | {(k.Slot.Length > 0 ? "`" + k.Slot + "`" : "**нет**")} | {Cnt(a => a.KindHeadings.ContainsKey(k.Id))} | {(templateKinds.Contains(k.Id) ? "есть" : "нет")} |");

        s.AppendLine("\n## 3. Паспорт и индикаторы");
        s.AppendLine($"\n- Таблица паспорта найдена: {Cnt(a => a.Passport is not null)} из {text.Count}; с номерами заданий в последнем столбце: {Cnt(a => a.Passport is { TaskNumbers: true })}.");
        s.AppendLine($"- Таблица «компетенция — индикатор»: {Cnt(a => a.IndicatorsTable is not null)}; с «знать / уметь / владеть»: {Cnt(a => a.IndicatorsHaveSkills)}.");
        var prefixes = text.SelectMany(a => a.CodePrefixes.Keys.Select(k => (k, a.Doc.Id))).GroupBy(x => x.k).OrderByDescending(g => g.Count());
        s.AppendLine("- Префиксы кодов компетенций (файлов): " + string.Join(", ", prefixes.Select(g => $"{g.Key} — {g.Count()}")) + ".");
        s.AppendLine("\nШапки таблиц паспорта (варианты):\n");
        s.AppendLine("| Шапка | Файлов | ID |\n|---|---|---|");
        foreach (var g in text.Where(a => a.Passport is not null).GroupBy(a => Norm(a.Passport!.Header)).OrderByDescending(g => g.Count()).Take(8))
            s.AppendLine($"| {Md(g.First().Passport!.Header)} | {g.Count()} | {Ids(g)} |");
        var means = text.Where(a => a.Passport is not null).SelectMany(a => a.Passport!.Means.Select(m => (m, a.Doc.Id)))
            .GroupBy(x => x.m).OrderByDescending(g => g.Select(x => x.Id).Distinct().Count()).Take(12).ToList();
        s.AppendLine("\nОценочные средства в последнем столбце паспорта, первые 12 по числу файлов:\n");
        s.AppendLine("| Значение | Файлов |\n|---|---|");
        foreach (var g in means) s.AppendLine($"| {Md(Trim(g.Key, 80))} | {g.Select(x => x.Id).Distinct().Count()} |");

        s.AppendLine("\n## 4. Оценочные средства и формы контроля (слова в тексте)");
        s.AppendLine("\n| Признак | Файлов | `means_type` схемы |\n|---|---|---|");
        foreach (var (name, _, enumValue) in MeansWords)
            s.AppendLine($"| {name} | {Cnt(a => a.MeansWordCounts.ContainsKey(name))} | {(enumValue.Length > 0 ? "`" + enumValue + "`" : "**нет в перечне**")} |");

        s.AppendLine("\n## 5. Банк заданий и нумерация");
        s.AppendLine();
        foreach (var g in text.GroupBy(NumberingKind).OrderByDescending(g => g.Count()))
            s.AppendLine($"- {g.Key}: {g.Count()} ({Ids(g)})");
        s.AppendLine($"\n- Варианты ответа теста (8+ строк «а) …»): {Cnt(a => a.OptionLines >= 8)}; тесты в формате системы тестирования (вопрос с типом, ответы с весом): {Cnt(a => a.LmsQuestions > 0)}; " +
                     $"строки «ответ / эталон»: {Cnt(a => a.AnswerLines > 0)}; баллы за задание: {Cnt(a => a.ScoreMentions > 0)}; " +
                     $"коды индикаторов в тексте заданий: {Cnt(a => a.IndicatorRefsInText > 0)}; «семестр» в заголовках: {Cnt(a => a.SemesterHeadings > 0)}.");
        s.AppendLine("\nНазвания групп заданий и контроля (заголовки видов «задания», «группа», «промежуточная аттестация», «рефераты», «текущий контроль»), без цифр:\n");
        s.AppendLine("| Заголовок | Вид | Файлов | Пример |\n|---|---|---|---|");
        var groups = text.SelectMany(a => a.Outline.Where(o => o.Kind is "bank" or "group" or "intermediate" or "essay" or "current" or "means" or "scale")
                .Select(o => (Key: HeadingKey(o.Text), o.Text, o.Kind, a.Doc.Id)))
            .Where(x => x.Key.Length > 3).GroupBy(x => x.Key)
            .OrderByDescending(g => g.Select(x => x.Id).Distinct().Count()).Take(22);
        foreach (var g in groups)
            s.AppendLine($"| {Md(g.Key)} | {g.First().Kind} | {g.Select(x => x.Id).Distinct().Count()} | {Md(Trim(g.First().Text, 60))} |");

        s.AppendLine("\n## 6. Билеты");
        var withTickets = text.Where(a => a.Tickets > 0).ToList();
        if (withTickets.Count == 0) s.AppendLine("\nЭкзаменационных билетов нет ни в одном файле.");
        else
        {
            s.AppendLine($"\n- Файлов с билетами: {Cnt(a => a.Tickets > 0)} из {text.Count} ({Ids(withTickets)}).");
            var perTicket = withTickets.SelectMany(a => a.TicketQuestions).GroupBy(q => q.Key).OrderBy(g => g.Key);
            s.AppendLine("- Вопросов в билете (билетов): " + string.Join(", ", perTicket.Select(g => $"{g.Key} — {g.Sum(x => x.Value)}")) + ".");
            var header = withTickets.SelectMany(a => a.TicketHeader).GroupBy(h => h).OrderByDescending(g => g.Count());
            s.AppendLine("- Шапка билета (файлов): " + string.Join(", ", header.Select(g => $"{g.Key} — {g.Count()}")) + ".");
        }

        s.AppendLine("\n## 7. Шкалы и рейтинг");
        s.AppendLine($"\n- Пятибалльная: {Cnt(a => a.ScaleKinds.Contains("five_point"))}; зачёт / незачёт: {Cnt(a => a.ScaleKinds.Contains("pass_fail"))}; " +
                     $"стобалльная: {Cnt(a => a.ScaleKinds.Contains("hundred_point"))}; ни одной: {Cnt(a => a.ScaleKinds.Count == 0)}.");
        s.AppendLine($"- Баллы упоминаются: {Cnt(a => a.Points)}; недели: {Cnt(a => a.Weeks)}; таблицы шкал и критериев: {Cnt(a => a.Tables.Any(t => t.Role == "scale"))}; " +
                     $"таблицы с баллами (нормативы, рейтинг): {Cnt(a => a.Tables.Any(t => t.Role == "rating"))}.");

        s.AppendLine("\n## 8. Таблицы, формулы, рисунки");
        s.AppendLine($"\n- Формулы (OMML, MathType, поля EQ): {Cnt(a => a.Formulas > 0)} файлов, всего {text.Sum(a => a.Formulas)}; символы формул в тексте: {Cnt(a => a.MathSymbolParagraphs > 0)} файлов.");
        s.AppendLine($"- Крупные рисунки (от 4,5 см): {Cnt(a => a.Drawings > 0)} файлов, всего {text.Sum(a => a.Drawings)}; мелких (эмблемы): {text.Sum(a => a.SmallDrawings)}. Для PDF размер взят по картинке на странице.");
        s.AppendLine($"- Таблицы вне паспорта, подписей, шкал и рейтинга: {Cnt(a => a.TaskTables > 0)} файлов, всего {text.Sum(a => a.TaskTables)}; PDF таблиц не показывает.");
        s.AppendLine($"- Таблицы с объединёнными ячейками (кроме подписей и оформительских): {Cnt(a => a.MergedTables > 0)} файлов, всего {text.Sum(a => a.MergedTables)}.");

        s.AppendLine("\n## 9. Что не ложится в схему (по числу файлов)");
        s.AppendLine("\n| Находка | Файлов | Примеры |\n|---|---|---|");
        foreach (var g in text.SelectMany(a => a.NoSlot.Select(n => (n.Key, n.Detail, a.Doc.Id, a.Standard))).GroupBy(x => x.Key)
                     .OrderByDescending(g => g.Select(x => x.Id).Distinct().Count()))
        {
            var n3 = g.Where(x => x.Standard == "3++").Select(x => x.Id).Distinct().Count();
            s.AppendLine($"| {Md(g.Key)} | {g.Select(x => x.Id).Distinct().Count()}{(n3 > 0 ? $" (3++: {n3})" : "")} | {Md(Trim(string.Join("; ", g.Take(3).Select(x => $"{x.Id}: {x.Detail}")), 170))} |");
        }

        s.AppendLine("\n## 10. Что есть в схеме и не встречается");
        s.AppendLine("\nСколько файлов имеют признак элемента схемы. Малое число или ноль — элемент схемы ничем не подтверждён настоящими документами.\n");
        s.AppendLine("| Элемент схемы | Признак | Файлов |\n|---|---|---|");
        foreach (var (element, sign, pred) in SchemaCoverage()) s.AppendLine($"| `{element}` | {sign} | {Cnt(pred)} из {text.Count} |");

        s.AppendLine("\n## 11. Заголовки без вида (по числу файлов)");
        s.AppendLine("\nЗаголовки, которые ни одно правило не отнесло к виду раздела; вверху — то, что стоит добавить в правила или в схему.\n");
        s.AppendLine("| Заголовок (без цифр) | Файлов | Пример |\n|---|---|---|");
        var others = text.SelectMany(a => a.Outline.Where(o => o.Kind == "other").Select(o => (Key: HeadingKey(o.Text), o.Text, a.Doc.Id)))
            .Where(x => x.Key.Length > 3).GroupBy(x => x.Key).OrderByDescending(g => g.Select(x => x.Id).Distinct().Count()).Take(40);
        foreach (var g in others) s.AppendLine($"| {Md(g.Key)} | {g.Select(x => x.Id).Distinct().Count()} | {Md(Trim(g.First().Text, 60))} |");
        return s.ToString();
    }

    /// <summary>Пометка файла, чей номер закреплён, а самого файла в папке нет.</summary>
    public const string MissingMark = "файла нет в папке";

    /// <summary>В статистику идут ФОС с текстом; рабочие программы из этой папки и точные копии не считаются.</summary>
    public static bool InStatistics(FosAnalysis a) =>
        a.Doc.Error is null && a.Doc.TextChars >= 300 && a.DocKind != "РПД" && a.DuplicateOf is null;

    private static string NoteOf(FosAnalysis a) =>
        a.Doc.Error ?? (a.DuplicateOf is not null ? $"точная копия {a.DuplicateOf}" : a.DocKind == "РПД" ? "рабочая программа, не ФОС" : a.Doc.TextChars < 300 ? "текста нет" : "");

    /// <summary>Дисциплина без уточнения в скобках и после двоеточия: «Прикладная физическая культура: Спортивные игры» и «… (Интеллектуальные виды спорта)» — одна.</summary>
    private static string Family(FosAnalysis a)
    {
        if (a.Doc.Error == MissingMark) return "(файла нет в папке)";
        if (a.Doc.Error is not null) return "(не прочитан)";
        if (a.DocKind == "РПД") return "(рабочая программа, не ФОС)";
        if (a.Doc.TextChars < 300) return "(скан, текста нет)";
        var n = Norm(a.Discipline);
        var cut = n.IndexOfAny([':', '(']);
        if (cut > 3) n = n[..cut];
        n = n.Trim(' ', '«', '»', '"', '.');
        return n.Length > 0 ? n : "(название не найдено)";
    }

    private static IEnumerable<(string Element, string Sign, Func<FosAnalysis, bool> Pred)> SchemaCoverage()
    {
        yield return ("title_page", "направление и дисциплина на титуле", a => a.Direction.Length > 0 && a.Discipline.Length > 0);
        yield return ("approval_sheet", "заголовок согласования, ФИО составителя или таблицы подписей", a => a.KindHeadings.ContainsKey("approval") || a.Authors > 0 || a.Tables.Any(t => t.Role == "signatures"));
        yield return ("competence_indicators", "таблица «компетенция — индикатор»", a => a.IndicatorsTable is not null);
        yield return ("passport.sections", "таблица паспорта", a => a.Passport is not null);
        yield return ("passport.sections[].tasks", "номера заданий в последнем столбце паспорта", a => a.Passport is { TaskNumbers: true });
        yield return ("passport.indicators[].rpd_section_numbers", "таблица индикаторов с 3+ столбцами", a => a.IndicatorsTable is { Cols: >= 3 });
        yield return ("current_control.forms", "раздел «Текущий контроль»", a => a.KindHeadings.ContainsKey("current"));
        yield return ("current_control.variants", "заголовки «Вариант …»", a => a.Outline.Any(o => Regex.IsMatch(Norm(o.Text), @"вариант")));
        yield return ("current_control.rating", "баллы и недели вместе", a => a.Points && a.Weeks);
        yield return ("evaluation_means", "раздел «Оценочные средства»", a => a.KindHeadings.ContainsKey("means"));
        yield return ("evaluation_means[].evaluation_criteria", "раздел критериев или таблица шкалы", a => a.KindHeadings.ContainsKey("scale") || a.Tables.Any(x => x.Role == "scale"));
        yield return ("task_bank", "серия 3+ нумерованных пунктов", a => a.Runs.Any(r => r.Length >= 3));
        yield return ("task_bank[].semester", "«семестр» в заголовке", a => a.SemesterHeadings > 0);
        yield return ("task_bank[].tasks[].expected_answer", "строки «ответ / эталон» или ответы с весом", a => a.AnswerLines > 0 || a.LmsAnswers > 0);
        yield return ("task_bank[].tasks[].max_score", "баллы у заданий", a => a.ScoreMentions > 0);
        yield return ("task_bank[].tasks[].indicator_codes", "коды индикаторов в тексте заданий", a => a.IndicatorRefsInText > 0);
        yield return ("exam_tickets", "билеты", a => a.Tickets > 0);
        yield return ("evaluation_scale.five_point", "«отлично … хорошо … удовлетворительно»", a => a.ScaleKinds.Contains("five_point"));
        yield return ("evaluation_scale.pass_fail", "«зачтено / не зачтено»", a => a.ScaleKinds.Contains("pass_fail"));
        yield return ("evaluation_scale.hundred_point", "«100 баллов» или «0–100»", a => a.ScaleKinds.Contains("hundred_point"));
        foreach (var (name, _, enumValue) in MeansWords.Where(m => m.EnumValue.Length > 0))
            yield return ($"evaluation_means[].means_type = {enumValue}", name, a => a.MeansWordCounts.ContainsKey(name));
    }

    /// <summary>Нумерация заданий по сериям: по группам с 1, сквозная через заголовки или одна серия.</summary>
    public static string NumberingKind(FosAnalysis a)
    {
        var series = a.Runs.Where(r => r.Length >= 3).ToList();
        if (series.Count == 0) return "серий нумерации нет";
        var fromOne = series.Count(r => r.Start == 1);
        var through = series.Any(r => r.HeadingsInside > 0 || r.Start > 1);
        if (series.Count == 1) return through ? "одна серия через заголовки" : "одна серия";
        if (fromOne >= 2 && through) return "смешанная: серии с 1 и сквозные";
        return fromOne >= 2 ? "по группам, каждая с 1" : "сквозная";
    }

    // ---------- Таблицы для Excel ----------

    private static string FilesCsv(IReadOnlyList<FosAnalysis> items)
    {
        var s = new StringBuilder();
        s.AppendLine("id;файл;формат;вид;копия;страниц;абзацев;таблиц;направление;уровень;форма;год;фгос;частей;дисциплина;паспорт;индикаторы;билетов;серий;нумерация;формул;крупных рисунков;мелких рисунков;виды;ошибка");
        foreach (var a in items)
            s.AppendLine(string.Join(';', new[]
            {
                a.Doc.Id, MaskFileName(a.Doc.FileName), a.Doc.Format, a.DocKind, a.DuplicateOf ?? "", a.Doc.Pages?.ToString() ?? "", a.Doc.Paragraphs.Count().ToString(),
                a.Doc.Tables.Count().ToString(), a.Direction, a.Level, a.StudyForm, a.Year, a.Standard, a.Parts.ToString(), a.Discipline,
                a.Passport is null ? "" : $"{a.Passport.Rows}x{a.Passport.Cols}", a.IndicatorsTable is null ? "" : $"{a.IndicatorsTable.Rows}x{a.IndicatorsTable.Cols}",
                a.Tickets.ToString(), a.Runs.Count(r => r.Length >= 3).ToString(), NumberingKind(a), a.Formulas.ToString(), a.Drawings.ToString(),
                a.SmallDrawings.ToString(), string.Join(",", a.KindHeadings.Keys), a.Doc.Error ?? ""
            }.Select(Csv)));
        return s.ToString();
    }

    private static string HeadingsCsv(IReadOnlyList<FosAnalysis> items)
    {
        var s = new StringBuilder();
        s.AppendLine("id;блок;вид;ключ;заголовок;блоков");
        foreach (var a in items)
            foreach (var o in a.Outline)
                s.AppendLine(string.Join(';', new[] { a.Doc.Id, o.Block.ToString(), o.Kind, HeadingKey(o.Text), o.Text, o.Size.ToString() }.Select(Csv)));
        return s.ToString();
    }

    // ---------- Файл оформления ----------

    private static string Template(FosAnalysis t, IReadOnlyList<FosAnalysis> items)
    {
        var d = t.Doc;
        var s = new StringBuilder();
        s.AppendLine("# Файл оформления ФОС: разделы и закладки");
        s.AppendLine();
        s.AppendLine($"{MaskFileName(d.FileName)} · блоков {d.Blocks.Count} · закладок {d.Bookmarks.Count}. Раздел файла — участок между разрывами раздела Word.");

        s.AppendLine("\n## Закладки");
        s.AppendLine("\n| Закладка | Блоки | Что охватывает |\n|---|---|---|");
        foreach (var b in d.Bookmarks)
        {
            var span = b.EndBlock - b.StartBlock + 1;
            var first = d.Blocks.FirstOrDefault(x => x.Index == b.StartBlock) is FosParagraph fp ? Trim(fp.Text, 50) : "таблица";
            s.AppendLine($"| `{b.Name}` | {b.StartBlock}–{b.EndBlock} | {(span > 6 ? $"**{span} блоков**: " : "")}{Md(first)} |");
        }

        s.AppendLine("\n## Разделы файла (по разрывам раздела Word)");
        s.AppendLine("\n| № | Начало раздела | Закладок | Пустых заготовок («1…», «…») | Таблиц | Таблиц вне закладок |\n|---|---|---|---|---|---|");
        var boundaries = d.Paragraphs.Where(p => p.SectionEnd).Select(p => p.Index).ToList();
        boundaries.Add(d.Blocks.Count - 1);
        var from = 0;
        var n = 0;
        var boiler = new Regex(@"минобрнауки|федеральное государственное|высшего образования|московский государственный|фгбоу", RegexOptions.IgnoreCase);
        foreach (var to in boundaries)
        {
            var blocks = d.Blocks.Where(b => b.Index >= from && b.Index <= to).ToList();
            var title = string.Join(" / ", blocks.OfType<FosParagraph>().Select(p => p.Text)
                .Where(x => x.Length > 3 && !boiler.IsMatch(x)).Take(2).Select(x => Trim(x, 45)));
            var marks = d.Bookmarks.Where(b => b.StartBlock >= from && b.StartBlock <= to).ToList();
            var placeholders = blocks.OfType<FosParagraph>().Count(p => Regex.IsMatch(p.Text, @"^(\d+\s*[.)]?\s*)?[…]+\s*$|^\d+\s*[.)]\s*$"));
            var tables = blocks.OfType<FosTable>().ToList();
            var uncovered = tables.Count(x => !d.Bookmarks.Any(b => b.StartBlock <= x.Index && b.EndBlock >= x.Index));
            n++;
            s.AppendLine($"| {n} | {Md(title)} | {marks.Count} | {placeholders} | {tables.Count} | {uncovered} |");
            from = to + 1;
        }

        s.AppendLine("\n## Таблицы файла");
        foreach (var x in t.Tables)
        {
            var covered = d.Bookmarks.FirstOrDefault(b => b.StartBlock <= x.Block && b.EndBlock >= x.Block);
            s.AppendLine($"- блок {x.Block}: {x.Role}, {x.Rows}×{x.Cols}, закладка: {(covered is null ? "**нет**" : "`" + covered.Name + "`")}; шапка: {Md(x.Header)}");
        }

        s.AppendLine("\n## Разделы настоящих ФОС, которых нет среди заголовков файла оформления");
        s.AppendLine();
        var have = t.KindHeadings.Keys.ToHashSet();
        var withText = items.Where(InStatistics).ToList();
        foreach (var k in Kinds.Where(k => !have.Contains(k.Id)))
            s.AppendLine($"- {k.Title}: в {withText.Count(a => a.KindHeadings.ContainsKey(k.Id))} из {withText.Count} файлов");
        return s.ToString();
    }

    // ---------- Мелочи ----------

    private static string KindTitle(string id) => Kinds.FirstOrDefault(k => k.Id == id)?.Title ?? "без вида";
    private static string Or(string v) => v.Length > 0 ? v : "?";
    private static string YesNo(bool v) => v ? "да" : "нет";
    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;
    private static string Md(string v) => v.Replace("|", "\\|").Replace("\n", " ");
    private static string Csv(string v) => v.Contains(';') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
