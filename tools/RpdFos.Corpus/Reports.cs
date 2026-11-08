using System.Text;
using System.Text.RegularExpressions;
using DisciplineDataExtractor;

namespace RpdFos.Corpus;

/// <summary>Отчёты прогона: сводка, по файлу на план и на РПД, машиночитаемый список находок.</summary>
public static class Reports
{
    /// <summary>Разбор главных находок пишет автор; повторный прогон его не трогает.</summary>
    public const string MainFindings = "Главные находки.md";

    public static void Write(string output, IReadOnlyList<PlanReport> plans, IReadOnlyList<RpdReport> rpds,
        IReadOnlyList<(string Title, List<string> Files)> visual, TimeSpan elapsed, DateTimeOffset started)
    {
        Directory.CreateDirectory(Path.Combine(output, "Планы"));
        Directory.CreateDirectory(Path.Combine(output, "РПД"));
        foreach (var p in plans) File.WriteAllText(Path.Combine(output, "Планы", Safe(p.Set.Name) + ".md"), Plan(p), Encoding.UTF8);
        foreach (var r in rpds) File.WriteAllText(Path.Combine(output, "РПД", Safe(Path.GetFileNameWithoutExtension(r.File)) + ".md"), Rpd(r), Encoding.UTF8);

        var findings = plans.SelectMany(p => p.Findings).Concat(rpds.SelectMany(r => r.Findings)).ToList();
        var csv = new StringBuilder("План или документ;Вид;Место;Сообщение;Класс\n");
        foreach (var f in findings) csv.AppendLine(string.Join(';', new[] { f.Subject, f.Kind, f.Place, f.Message, f.Class }.Select(Csv)));
        File.WriteAllText(Path.Combine(output, "находки.csv"), csv.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(output, "Сводка.md"), Summary(plans, rpds, visual, findings, elapsed, started), Encoding.UTF8);
    }

    private static string Summary(IReadOnlyList<PlanReport> plans, IReadOnlyList<RpdReport> rpds,
        IReadOnlyList<(string Title, List<string> Files)> visual, List<Finding> findings, TimeSpan elapsed, DateTimeOffset started)
    {
        var s = new StringBuilder();
        s.AppendLine("# Прогон корпуса — сводка");
        s.AppendLine();
        s.AppendLine($"Начат {started.ToLocalTime():dd.MM.yyyy HH:mm}, длительность {elapsed:hh\\:mm\\:ss}. Отчёт сформирован инструментом `tools/RpdFos.Corpus`; повторный прогон его перезаписывает.");
        s.AppendLine();
        s.AppendLine($"- Комплектов прогнано: {plans.Count}, план создан: {plans.Count(p => p.HasPlan)}.");
        s.AppendLine($"- Профиль подставлен (разбор его не нашёл, взят из комплекта — запасной путь): {plans.Count(p => p.SubstitutedProfile is not null)}.");
        s.AppendLine($"- Статусы импорта: {string.Join(", ", plans.GroupBy(p => p.Status).Select(g => $"{g.Key} — {g.Count()}"))}.");
        s.AppendLine($"- РПД создано по дисциплинам планов: {plans.Sum(p => p.Disciplines.Count(d => d.Document == "создан"))}, напечатано без сбоя: {plans.Sum(p => p.Disciplines.Count(d => d.Print.StartsWith("сформирован")))}.");
        s.AppendLine($"- Настоящих РПД: {rpds.Count}, сравнено: {rpds.Count(r => r.Status.StartsWith("сравнено"))}.");
        s.AppendLine();
        s.AppendLine("Классы находок ставятся правилами инструмента — это предложение для обсуждения.");
        s.AppendLine();

        // Ключевые виды — для таблиц «до / после» этапа исправлений: считаются одинаково от прогона к прогону.
        s.AppendLine("## Ключевые виды");
        s.AppendLine();
        s.AppendLine("| Вид | Сколько |");
        s.AppendLine("| --- | ---: |");
        int Count(Func<Finding, bool> f) => findings.Count(f);
        foreach (var (title, value) in new (string, int)[]
                 {
                     ("Профиль не распознан (планов)", Count(f => f.Message == "Не распознано: профиль")),
                     ("Контактные часы по семестрам не равны итогу", Count(f => f.Message.Contains("контактные часы по семестрам"))),
                     ("hours_match_curriculum в новых РПД", Count(f => f.Kind == "hours_match_curriculum")),
                     ("Индикаторы без типа (знать / уметь / владеть)", Count(f => f.Message.Contains("тип (знать / уметь / владеть) не распознан"))
                         + findings.Where(f => f.Message.StartsWith("Индикаторов без типа")).Sum(f => int.Parse(Regex.Match(f.Message, @"\d+").Value))),
                     ("Дисциплины без кафедры", Count(f => f.Message.Contains("не загружена — дисциплина без кафедры"))),
                     ("Конфликты кафедр между планами", Count(f => f.Message.Contains("уже у кафедры"))),
                     ("Новые РПД с пустой кафедрой", Count(f => f.Place == "/title_page/department_name")),
                     ("Строки матрицы не найдены в плане (замечания)", Count(f => f.Message.Contains("не найдена в учебном плане") || f.Message.Contains("неоднозначна"))),
                     ("Строки матрицы без дисциплины (сохранены для привязки)", plans.Sum(p => p.UnmatchedRows.Count)),
                     ("Строки матрицы сопоставлены по похожести названия", Count(f => f.Message.Contains("по похожести названия"))),
                     ("Новые РПД без компетенций", plans.Sum(p => p.WithoutCompetences.Count)),
                     ("Расхождения часов с настоящими РПД (ошибка разбора)", Count(f => f.Kind == "расхождение" && f.Class == FindingClasses.ParseError))
                 })
            s.AppendLine($"| {title} | {value} |");
        s.AppendLine();

        s.AppendLine("## Находки по классам");
        s.AppendLine();
        s.AppendLine("| Класс | Планы | РПД | Всего |");
        s.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var c in FindingClasses.All)
        {
            var p = plans.Sum(x => x.Findings.Count(f => f.Class == c));
            var r = rpds.Sum(x => x.Findings.Count(f => f.Class == c));
            s.AppendLine($"| {c} | {p} | {r} | {p + r} |");
        }
        s.AppendLine();

        s.AppendLine("## Проверить в первую очередь");
        s.AppendLine();
        var partTime = plans.Where(p => p.Set.Form.Contains("заоч")).ToList();
        s.AppendLine($"- **Форма «Очно-заочная»:** комплектов {partTime.Count}, план создан {partTime.Count(p => p.HasPlan)}; форма распознана: {string.Join(", ", partTime.Select(p => $"{p.Set.Name} — {p.Parsed?.Plan?.StudyForm}"))}.");
        foreach (var p in plans.Where(p => p.Parsed?.Plan?.Level == EducationLevel.Specialist))
            s.AppendLine($"- **Специалитет {p.Set.Name}:** статус {p.Status}, семестров до {p.Parsed!.Disciplines.SelectMany(d => d.Semesters).Select(x => x.Semester).DefaultIfEmpty().Max()}, дисциплин {p.Parsed.Disciplines.Count}.");
        var old = plans.Where(p => p.Set.Plan.StartsWith("_преобразовано")).ToList();
        s.AppendLine($"- **Старые планы после преобразования .xls:** {string.Join("; ", old.Select(p => $"{p.Set.Name} — {p.Status}"))}.");
        var modules = plans.Where(p => p.Parsed?.Plan?.Level == EducationLevel.Bachelor)
            .Sum(p => p.Parsed!.Disciplines.Count(d => d.Kind == EntryKind.Module));
        var children = plans.Where(p => p.Parsed?.Plan?.Level == EducationLevel.Bachelor)
            .Sum(p => p.Parsed!.Disciplines.Count(d => d.ParentCode is not null));
        s.AppendLine($"- **Модули в бакалавриате:** модулей {modules}, дочерних строк {children}.");
        var zero = plans.SelectMany(p => p.Findings.Where(f => f.Kind == "0 з. е.")).ToList();
        s.AppendLine($"- **0 з. е.:** {zero.Count} строк планов, например: {string.Join("; ", zero.Take(3).Select(f => f.Message))}.");
        s.AppendLine();

        var choices = plans.Where(p => p.MatrixChoice.Count > 0).ToList();
        if (choices.Count > 0)
        {
            s.AppendLine("## Выбор матрицы для комплектов без неё");
            s.AppendLine();
            foreach (var p in choices) s.AppendLine($"- **{p.Set.Name}:** {string.Join("; ", p.MatrixChoice)}.");
            s.AppendLine();
        }

        s.AppendLine("## Планы");
        s.AppendLine();
        s.AppendLine("| План | Уровень | Форма | Год | Статус | Дисциплин | РПД / печать | Несопост. строк МК | Без компетенций | " +
                     string.Join(" | ", FindingClasses.All) + " |");
        s.AppendLine("| --- | --- | --- | ---: | --- | ---: | --- | ---: | ---: | " + string.Join(" | ", FindingClasses.All.Select(_ => "---:")) + " |");
        foreach (var p in plans)
        {
            var info = p.Parsed?.Plan;
            s.AppendLine($"| [{p.Set.Name}](Планы/{Uri.EscapeDataString(Safe(p.Set.Name))}.md) | {info?.Level} | {info?.StudyForm} | {info?.Year} | {p.Status} | " +
                         $"{p.Parsed?.Disciplines.Count} | {p.Disciplines.Count(d => d.Document == "создан")} / {p.Disciplines.Count(d => d.Print.StartsWith("сформирован"))} | " +
                         $"{p.UnmatchedRows.Count} | {p.WithoutCompetences.Count} | " +
                         string.Join(" | ", FindingClasses.All.Select(c => p.Findings.Count(f => f.Class == c))) + " |");
        }
        s.AppendLine();

        s.AppendLine("## Настоящие РПД");
        s.AppendLine();
        s.AppendLine("| Документ | Статус | План | Дисциплина | Расхождений |");
        s.AppendLine("| --- | --- | --- | --- | ---: |");
        foreach (var r in rpds)
            s.AppendLine($"| [{r.File}](РПД/{Uri.EscapeDataString(Safe(Path.GetFileNameWithoutExtension(r.File)))}.md) | {r.Status} | {r.Plan} | {r.Discipline} | {r.Rows.Count(x => x.Class is not null)} |");
        s.AppendLine();

        if (visual.Count > 0)
        {
            s.AppendLine("## Наглядное сравнение");
            s.AppendLine();
            foreach (var (title, files) in visual) s.AppendLine($"- **{title}:** {string.Join("; ", files)}");
            s.AppendLine();
        }

        // Частые сообщения — чтобы увидеть классы находок без чтения всех отчётов.
        s.AppendLine("## Частые находки (кроме «ожидаемо»)");
        s.AppendLine();
        s.AppendLine("| Класс | Вид | Сообщение (обобщено) | Сколько |");
        s.AppendLine("| --- | --- | --- | ---: |");
        foreach (var g in findings.Where(f => f.Class != FindingClasses.Expected)
                     .GroupBy(f => (f.Class, f.Kind, Pattern: Generalize(f.Message))).OrderByDescending(g => g.Count()).Take(40))
            s.AppendLine($"| {g.Key.Class} | {g.Key.Kind} | {Md(g.Key.Pattern)} | {g.Count()} |");
        return s.ToString();
    }

    private static string Plan(PlanReport p)
    {
        var s = new StringBuilder();
        var info = p.Parsed?.Plan;
        s.AppendLine($"# {p.Set.Name}");
        s.AppendLine();
        s.AppendLine($"- План: `{p.Set.Plan}`; список: `{p.Set.CompetenceList}`; матрица: `{p.Matrix ?? "—"}`{(p.Set.OlderMatrix ? " (другого года)" : "")}.");
        foreach (var c in p.MatrixChoice) s.AppendLine($"  - {c}");
        s.AppendLine($"- Статус импорта: **{p.Status}**, прогон {p.Elapsed.TotalSeconds:0} с.");
        if (p.SubstitutedProfile is not null)
            s.AppendLine($"- **Профиль не распознан разбором** — без него план не создаётся. Для прогона подставлен «{p.SubstitutedProfile}» (из комплекта или имени файла).");
        if (info is not null)
        {
            s.AppendLine($"- Распознано: направление {info.DirectionCode} «{info.DirectionName}», профиль «{info.Profile}», уровень {info.Level}, форма {info.StudyForm}, год {info.Year}, ФГОС {info.Fgos?.Order}.");
            var parsed = p.Parsed!;
            s.AppendLine($"- Дисциплин (строк плана): {parsed.Disciplines.Count}, семестров до {parsed.Disciplines.SelectMany(d => d.Semesters).Select(x => x.Semester).DefaultIfEmpty().Max()}, компетенций {parsed.Competences.Count}, индикаторов {parsed.Competences.Sum(c => c.Indicators.Count)}, связей матрицы {parsed.Matrix.Count}.");
            s.AppendLine($"- Замечаний разбора: ошибок {parsed.Issues.Count(i => i.Severity == IssueSeverity.Error)}, предупреждений {parsed.Issues.Count(i => i.Severity == IssueSeverity.Warning)}.");
        }
        s.AppendLine();

        if (p.UnmatchedRows.Count > 0)
        {
            s.AppendLine($"## Несопоставленные строки матрицы ({p.UnmatchedRows.Count})");
            s.AppendLine();
            foreach (var r in p.UnmatchedRows) s.AppendLine($"- {Md(r)}");
            s.AppendLine();
        }
        if (p.WithoutCompetences.Count > 0)
        {
            s.AppendLine($"## Дисциплины без компетенций ({p.WithoutCompetences.Count})");
            s.AppendLine();
            foreach (var r in p.WithoutCompetences) s.AppendLine($"- {Md(r)}");
            s.AppendLine();
        }

        var notable = p.Findings.Where(f => f.Class != FindingClasses.Expected).ToList();
        s.AppendLine($"## Находки ({notable.Count})");
        s.AppendLine();
        if (notable.Count > 0)
        {
            s.AppendLine("| Класс | Вид | Место | Сообщение |");
            s.AppendLine("| --- | --- | --- | --- |");
            foreach (var f in notable) s.AppendLine($"| {f.Class} | {f.Kind} | {Md(f.Place)} | {Md(f.Message)} |");
            s.AppendLine();
        }
        var expected = p.Findings.Where(f => f.Class == FindingClasses.Expected).ToList();
        if (expected.Count > 0)
        {
            s.AppendLine($"## Ожидаемое ({expected.Count})");
            s.AppendLine();
            foreach (var f in expected) s.AppendLine($"- {f.Kind}: {Md(f.Message)}");
            s.AppendLine();
        }
        if (p.PrintWarnings.Count > 0)
        {
            s.AppendLine("## Предупреждения печати (по всем новым РПД плана)");
            s.AppendLine();
            foreach (var (w, n) in p.PrintWarnings.OrderByDescending(x => x.Value)) s.AppendLine($"- {Md(w)} — {n}");
            s.AppendLine();
        }
        if (p.Disciplines.Count > 0)
        {
            s.AppendLine("## Дисциплины");
            s.AppendLine();
            s.AppendLine("| Индекс | Дисциплина | Семестры | З. е. | Часов | Аттестация | Лаб. | Практ. | КСР/ИКР | Компетенций | РПД | Находок из плана | Печать |");
            s.AppendLine("| --- | --- | --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | --- | ---: | --- |");
            foreach (var d in p.Disciplines)
                s.AppendLine($"| {d.Code} | {Md(d.Name)} | {d.Semesters} | {d.Credits} | {d.Hours} | {Md(d.Attestation)} | {d.Labs} | {d.Practice} | {d.Ksr} | {d.Competences} | {d.Document} | {d.PlanIssues} | {Md(d.Print)} |");
        }
        return s.ToString();
    }

    private static string Rpd(RpdReport r)
    {
        var s = new StringBuilder();
        s.AppendLine($"# {r.File}");
        s.AppendLine();
        s.AppendLine($"- Статус: **{r.Status}**");
        if (r.Source is not null) s.AppendLine($"- Источник: `{r.Source}`");
        s.AppendLine($"- Направление: {r.Direction ?? "не определено"}, год документа: {r.Year?.ToString() ?? "не определён"}");
        if (r.Plan is not null) s.AppendLine($"- План: {r.Plan} (набор {r.PlanYear}); дисциплина: {r.Discipline} — найдена {r.Match}");
        foreach (var n in r.Notes) s.AppendLine($"- {n}");
        s.AppendLine();
        if (r.Rows.Count > 0)
        {
            s.AppendLine("| Поле | В настоящей РПД | У нас | Класс |");
            s.AppendLine("| --- | --- | --- | --- |");
            foreach (var row in r.Rows)
                s.AppendLine($"| {row.Field} | {Md(row.Real)} | {Md(row.Ours)} | {row.Class ?? "совпадает"} |");
            s.AppendLine();
        }
        var other = r.Findings.Where(f => f.Kind != "расхождение").ToList();
        if (other.Count > 0)
        {
            s.AppendLine("## Прочее");
            s.AppendLine();
            foreach (var f in other) s.AppendLine($"- {f.Class}: {f.Kind} — {Md(f.Message)}");
        }
        return s.ToString();
    }

    // Обобщение сообщения для группировки: числа, коды и названия в кавычках — заменителями.
    private static string Generalize(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Replace(message, "«[^»]*»", "«…»");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"[А-ЯA-Z]{1,4}\d?(\.[\wА-Яа-я()]+)+", "<код>");
        m = System.Text.RegularExpressions.Regex.Replace(m, @"\d+", "N");
        return m.Length > 160 ? m[..160] + "…" : m;
    }

    private static string Csv(string v) => v.Contains(';') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    private static string Md(string v) => v.Replace("|", "\\|").Replace("\n", " ");
    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
