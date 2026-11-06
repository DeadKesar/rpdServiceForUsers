using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.Printing;

namespace RpdFos.Corpus;

/// <summary>Строка сравнения: поле, в настоящей РПД, у нас, класс расхождения (null — совпадает).</summary>
public sealed record CompareRow(string Field, string Real, string Ours, string? Class);

public sealed class RpdReport(string file)
{
    public string File { get; } = file;
    public string Status { get; set; } = "";
    public string? Source { get; set; }
    public string? Direction { get; set; }
    public int? Year { get; set; }
    public string? Plan { get; set; }
    public int? PlanYear { get; set; }
    public string? Discipline { get; set; }
    public string? Match { get; set; }
    public List<CompareRow> Rows { get; } = [];
    public List<Finding> Findings { get; } = [];
    public List<string> Notes { get; } = [];
    public Guid? DocumentId { get; set; }
}

/// <summary>
/// Сравнение с настоящими РПД (раздел C): направление, год и дисциплина — из текста документа и имени файла; план —
/// корпусный того же направления, ближайший по году; наша РПД — новая, предзаполненная из плана. Сравнивается то, что
/// берётся из плана и справочников. Извлечение из настоящих документов эвристическое: чего не удалось прочитать,
/// то отмечено «не определено», а не расхождением.
/// </summary>
public sealed partial class RpdComparer(Workbench bench, string corpus, IReadOnlyList<PlanReport> plans)
{
    private readonly Dictionary<Guid, Guid> _templates = [];

    public async Task<IReadOnlyList<RpdReport>> RunAsync()
    {
        var folder = Path.Combine(corpus, "РПД");
        var reports = new List<RpdReport>();
        foreach (var path in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            var report = new RpdReport(Path.GetFileName(path));
            try
            {
                await CompareAsync(report, path);
            }
            catch (Exception e)
            {
                report.Status = "сбой сравнения";
                report.Findings.Add(new(report.File, "сбой", "", e.GetBaseException().Message, FindingClasses.ParseError));
            }
            reports.Add(report);
        }
        // Несколько настоящих РПД одной дисциплины. Обычная и для лиц с ОВЗ — вопрос модели: у нас документ по
        // дисциплине один. Остальное (копии файла, РПД других профилей того же направления при одном плане в
        // корпусе) — ожидаемо.
        foreach (var group in reports.Where(r => r.DocumentId is not null).GroupBy(r => r.DocumentId).Where(g => g.Count() > 1))
        {
            var ovz = group.Any(r => r.File.Contains("ОВЗ")) && group.Any(r => !r.File.Contains("ОВЗ"));
            foreach (var r in group)
                r.Findings.Add(ovz
                    ? new(r.File, "РПД для лиц с ОВЗ", r.Discipline ?? "",
                        $"У дисциплины обычная РПД и РПД для лиц с ОВЗ: {string.Join(", ", group.Select(x => x.File))}. В модели — один документ на дисциплину",
                        FindingClasses.ModelGap)
                    : new(r.File, "несколько РПД одной дисциплины", r.Discipline ?? "",
                        $"С той же дисциплиной плана сопоставлены: {string.Join(", ", group.Select(x => x.File))} (копии или другие профили направления)",
                        FindingClasses.Expected));
        }
        return reports;
    }

    private async Task CompareAsync(RpdReport report, string path)
    {
        var name = report.File;
        if (name.Contains("РП ГИА") || name.Contains("РП_ПП"))
        {
            report.Status = "пропущено: не РПД (программа ГИА или практики — будущие типы документов)";
            return;
        }
        var source = path;
        if (Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase))
        {
            var converted = Path.Combine(corpus, "_преобразовано", "РПД", Path.GetFileNameWithoutExtension(path) + ".docx");
            if (!System.IO.File.Exists(converted))
            {
                report.Status = "не сравнивалось: .doc без преобразованной копии";
                return;
            }
            source = converted;
            report.Notes.Add("Взята копия из _преобразовано (.doc → .docx).");
        }
        report.Source = Path.GetRelativePath(corpus, source);
        var content = DocText.Read(source);
        if (content.Text.Trim().Length < 200)
        {
            report.Status = "не сравнивалось: текст не извлечён (скан?)";
            return;
        }
        // PDF со шрифтами без таблицы Unicode даёт текст без кириллицы: по нему находилась «Информатика» из обрывков.
        if (Regex.Count(content.Text, "[а-яА-ЯёЁ]") < 200)
        {
            report.Status = "не сравнивалось: в извлечённом тексте нет кириллицы (шрифты PDF без таблицы Unicode)";
            return;
        }
        if (content.FromPdf) report.Notes.Add("PDF — только текст: таблицы часов и аттестации не сравнивались.");
        if (name.Contains("ОВЗ")) report.Notes.Add("Адаптированная версия для лиц с ОВЗ.");

        var head = content.Text.Length > 8000 ? content.Text[..8000] : content.Text;
        report.Direction = DirectionCode().Match(head) is { Success: true } dm ? dm.Value
            : DirectionCode().Match(name) is { Success: true } fm ? fm.Value : null;
        report.Year = YearOf(name, head);
        if (report.Direction is null)
        {
            report.Status = "не найдено: направление не определено";
            return;
        }

        var candidates = plans.Where(p => p.HasPlan && p.Parsed?.Plan?.DirectionCode == report.Direction)
            .OrderBy(p => report.Year is int y ? Math.Abs((p.Parsed!.Plan!.Year ?? 0) - y) : 0)
            .ThenByDescending(p => p.Parsed!.Plan!.Year).ToList();
        if (candidates.Count == 0)
        {
            report.Status = $"не найдено: плана направления {report.Direction} в корпусе нет";
            return;
        }

        // Название ищется сначала в титуле (после «Рабочая программа»): в начале текста упоминаются и другие
        // дисциплины — первый прогон сопоставил РПД ПФК с «Материаловедением». Титул обрывается на «Уровень»,
        // «Направление», «Цикл»: иначе название направления («Информатика и …») совпадает с дисциплиной.
        // Титул — в начале; «рабочая программа» дальше (PDF с нечитаемым титулом) — уже не титул.
        var titleAt = Regex.Match(head.Length > 3000 ? head[..3000] : head, @"рабоч\w*\s+программ\w*(\s+дисциплин\w*)?", RegexOptions.IgnoreCase);
        var titleEnd = new Regex(@"уровень|направлени|цикл\s+дисциплин|форма\s+обучени|квалификац", RegexOptions.IgnoreCase);
        var title = titleAt.Success ? head.Substring(titleAt.Index + titleAt.Length, Math.Min(1500, head.Length - titleAt.Index - titleAt.Length)) : "";
        if (titleEnd.Match(title) is { Success: true, Index: > 0 } end)
            title = title[..end.Index];
        // Без заголовка «Рабочая программа» (бывает в РПД ПФК) название — абзац перед «Уровень …» / «Направление …».
        if (!titleAt.Success && !content.FromPdf)
        {
            var lines = head[..Math.Min(3000, head.Length)].Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var at = lines.FindIndex(l => titleEnd.IsMatch(l));
            title = at > 0 ? lines[at - 1] : "";
        }

        await using var scope = bench.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var plan in candidates)
        {
            var disciplines = (await db.Disciplines.AsNoTracking().Where(d => d.CurriculumPlanId == plan.PlanId).ToListAsync())
                .Where(DocumentRules.CanHaveDocument).ToList();
            (Discipline Discipline, double Score) Best(string where, double min = 0.8) => disciplines
                .Select(d => (Discipline: d, Score: Score(d.Name, where)))
                .Where(x => x.Score >= min).OrderByDescending(x => x.Score).ThenByDescending(x => x.Discipline.Name.Length)
                .FirstOrDefault();
            // Похожесть по словам — в обе стороны: слова дисциплины в титуле и слова титула в дисциплине. Порог 0,8 —
            // лучше «не найдено», чем ложное совпадение («Администрирование в информационных системах»
            // против «Информационных систем и технологий»). Имя файла — только точным названием. Весь начальный текст
            // не просматривается: в нём название направления («Материаловедение и технологии материалов»).
            var best = Best(title);
            if (best.Discipline is null) best = Best(Path.GetFileNameWithoutExtension(name), min: 1.0001);
            if (best.Discipline is null) continue;
            report.Plan = plan.Set.Name;
            report.PlanYear = plan.Parsed!.Plan!.Year;
            report.Discipline = $"{best.Discipline.Code} {best.Discipline.Name}";
            report.Match = best.Score > 1 ? "по названию" : $"по словам названия ({best.Score:P0})";
            await CompareWithOursAsync(report, content, head, plan, best.Discipline);
            return;
        }
        report.Status = $"не найдено: дисциплина не найдена в планах направления {report.Direction} ({string.Join(", ", candidates.Select(c => c.Set.Name))})";
    }

    private async Task CompareWithOursAsync(RpdReport report, DocContent content, string head, PlanReport plan, Discipline discipline)
    {
        await using var scope = bench.Scope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        if (!_templates.TryGetValue(plan.PlanId!.Value, out var templateId))
            _templates[plan.PlanId.Value] = templateId = await bench.PublishTemplateAsync(plan.PlanId.Value);
        var created = await sp.GetRequiredService<DocumentCreation>().CreateAsync(templateId, discipline.Id, bench.RunnerId);
        var documentId = created.Document?.Id ?? created.ExistingId;
        if (documentId is not Guid id)
        {
            report.Status = $"наша РПД не создана: {created.Problem} {created.Detail}";
            return;
        }
        report.DocumentId = id;
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
        var facts = (await DisciplineFacts.LoadAsync(db, discipline.Id))!;
        var payload = JsonDocument.Parse(document.Payload).RootElement;
        var ours = DocumentSemesters.Of(payload, facts);

        // Год документа старше плана или компетенции прежнего ФГОС (ОК) — расхождения из-за версии, не ошибка системы.
        var realCodes = CompetenceCode().Matches(content.Text).Select(m => m.Groups[1].Value).Distinct().ToList();
        var older = report.Year is int y && report.PlanYear is int py && y < py;
        var oldFgos = realCodes.Any(c => c.StartsWith("ОК-"));
        if (older) report.Notes.Add($"Документ {report.Year} года старше плана {report.PlanYear} года.");
        if (oldFgos) report.Notes.Add("В документе компетенции прежнего ФГОС (ОК).");
        var diffClass = older || oldFgos ? FindingClasses.DocumentVersion : FindingClasses.SourceData;

        void Row(string field, string real, string our, bool same, string? cls = null)
        {
            report.Rows.Add(new(field, real, our, same ? null : cls ?? diffClass));
            if (!same) report.Findings.Add(new(report.File, "расхождение", field, $"в РПД: {real}; у нас: {our}", cls ?? diffClass));
        }

        var text = Norm(content.Text);
        // Название и индекс, блок и часть.
        Row("Название дисциплины", report.Match!, discipline.Name, true);
        var realCode = PlanCode().Match(content.Text) is { Success: true } cm ? cm.Value : "не указан";
        Row("Индекс", realCode, discipline.Code, realCode == "не указан" || Norm(realCode) == Norm(discipline.Code),
            realCode == "не указан" ? FindingClasses.Expected : null);
        var realPart = text.Contains("обязательной част") ? "обязательная часть"
            : text.Contains("формируемой участниками") ? "часть, формируемая участниками" : "не определено";
        var ourPart = discipline.PartType == PartTypes.Mandatory ? "обязательная часть"
            : discipline.PartType is PartTypes.Variable or PartTypes.ElectiveItem ? "часть, формируемая участниками" : discipline.PartType;
        Row("Часть", realPart, ourPart, realPart == "не определено" || realPart == ourPart);

        // Семестры, часы и аттестация — из таблицы трудоёмкости настоящей РПД.
        if (!content.FromPdf) Hours(report, content, ours, Row);

        // Компетенции: коды на уровне компетенций.
        var ourCodes = facts.Competences.Select(c => c.Code).Order().ToList();
        var onlyReal = realCodes.Except(ourCodes).Order().ToList();
        var onlyOurs = ourCodes.Except(realCodes).Order().ToList();
        Row("Компетенции", string.Join(", ", realCodes.Order()), string.Join(", ", ourCodes),
            onlyReal.Count == 0 && onlyOurs.Count == 0);

        // Лабораторные, практические, курсовое проектирование.
        var realCourse = CourseWork().IsMatch(content.Text);
        var ourCourse = ours.Any(s => s.ControlTypes.Any(t => t is ControlTypeCodes.CourseProject or ControlTypeCodes.CourseWork));
        Row("Курсовой проект или работа", realCourse ? "есть" : "нет", ourCourse ? "есть" : "нет", realCourse == ourCourse);
        var ourLabs = ours.Sum(s => s.LabHours) > 0;
        var realLabs = text.Contains("лабораторн");
        Row("Лабораторные работы (упоминание)", realLabs ? "есть" : "нет", ourLabs ? "есть" : "нет", !ourLabs || realLabs);

        // Титул: направление, профиль, уровень, квалификация, форма обучения, кафедра.
        void Title(string field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var found = text.Contains(Norm(value));
            Row(field, found ? "найдено в тексте" : "в тексте нет", value, found);
        }
        // Название направления, равное коду, — не прочитано разбором; подставленный прогоном профиль — не из плана.
        if (facts.Direction.Name != facts.Direction.Code) Title("Направление подготовки", facts.Direction.Name);
        else report.Notes.Add("Название направления в плане не прочитано — не сравнивалось.");
        if (plan.SubstitutedProfile is null) Title("Профиль", facts.Profile.Name);
        else report.Notes.Add("Профиль подставлен прогоном — не сравнивался.");
        Title("Уровень", PlanTitles.EducationLevel(facts.Direction.EducationLevel));
        Title("Квалификация", PlanTitles.Qualification(facts.Direction.EducationLevel));
        Title("Форма обучения", PlanTitles.StudyForm(facts.Plan.StudyForm));
        Title("Кафедра", facts.Department?.Name);

        report.Status = report.Rows.Any(r => r.Class is not null) ? "сравнено: есть расхождения" : "сравнено: совпадает";
    }

    // Таблица трудоёмкости: столбцы семестров по шапке, строки видов работы по подписи.
    private static void Hours(RpdReport report, DocContent content, IReadOnlyList<DisciplineSemesterDetails> ours,
        Action<string, string, string, bool, string?> row)
    {
        var table = content.Tables.FirstOrDefault(t => t.Any(r => r.Count > 1 && TotalRow().IsMatch(r[0])));
        if (table is null)
        {
            report.Notes.Add("Таблица трудоёмкости не найдена — часы не сравнивались.");
            return;
        }
        var columns = new Dictionary<int, int>();
        foreach (var r in table)
        {
            for (var i = 1; i < r.Count; i++)
                if (SemesterHeader().Match(r[i]) is { Success: true } m) columns.TryAdd(i, int.Parse(m.Groups[1].Value));
            if (columns.Count > 0) break;
        }
        if (columns.Count == 0)
        {
            // Один столбец значений без шапки семестров — дисциплина одного семестра.
            if (ours.Count == 1) columns[table.Max(r => r.Count) - 1] = ours[0].Semester;
            else
            {
                report.Notes.Add("В таблице трудоёмкости нет столбцов семестров — часы не сравнивались.");
                return;
            }
        }
        // Часы по семестрам у нас не прочитаны разбором — расхождения часов относятся к разбору.
        var unread = ours.All(s => s.Lectures + s.Practice + s.LabHours + s.SelfStudy == 0);
        if (unread) report.Notes.Add("Часы по семестрам в плане не прочитаны разбором.");
        var realSemesters = columns.Values.Distinct().Order().ToList();
        row("Семестры", string.Join(", ", realSemesters), string.Join(", ", ours.Select(s => s.Semester)),
            realSemesters.SequenceEqual(ours.Select(s => s.Semester)), null);

        (string Title, Regex Label, Func<DisciplineSemesterDetails, int> Value)[] kinds =
        [
            ("Лекции", Lectures(), s => s.Lectures), ("Практические", Practice(), s => s.Practice),
            ("Лабораторные", Labs(), s => s.LabHours), ("Самостоятельная работа", SelfStudy(), s => s.SelfStudy),
            ("Контроль", Control(), s => s.Control), ("Контактная работа", Contact(), s => s.ContactHours)
        ];
        foreach (var (title, label, value) in kinds)
        {
            var line = table.FirstOrDefault(r => r.Count > 1 && label.IsMatch(r[0]) && !Attestation().IsMatch(r[0]));
            if (line is null) continue;
            foreach (var (col, semester) in columns)
            {
                if (col >= line.Count) continue;
                var real = Number(line[col]);
                var our = ours.FirstOrDefault(s => s.Semester == semester) is { } s ? value(s) : 0;
                if (real is null) continue;
                row($"{title}, {semester} семестр", real.Value.ToString(), our.ToString(), real == our,
                    unread && real > 0 ? FindingClasses.ParseError : null);
            }
        }
        var attestation = table.FirstOrDefault(r => r.Count > 1 && Attestation().IsMatch(r[0]));
        if (attestation is not null)
            foreach (var (col, semester) in columns)
            {
                if (col >= attestation.Count) continue;
                var real = Kinds(attestation[col]);
                var our = ours.FirstOrDefault(s => s.Semester == semester)?.ControlTypes
                    .Where(t => t is ControlTypeCodes.Exam or ControlTypeCodes.Credit or ControlTypeCodes.GradedCredit).Order().ToList() ?? [];
                if (real.Count == 0) continue;
                row($"Аттестация, {semester} семестр", string.Join(", ", real.Select(PlanTitles.ControlType)),
                    string.Join(", ", our.Select(PlanTitles.ControlType)), real.SequenceEqual(our), null);
            }
    }

    private static List<string> Kinds(string cell)
    {
        var t = cell.ToLowerInvariant().Replace('ё', 'е');
        var result = new List<string>();
        if (t.Contains("экзамен")) result.Add(ControlTypeCodes.Exam);
        if (Regex.IsMatch(t, @"зач[её]т\s*с\s*оценк|дифф?\.?\s*зач|диф\.\s*зач")) result.Add(ControlTypeCodes.GradedCredit);
        else if (t.Contains("зачет")) result.Add(ControlTypeCodes.Credit);
        return result.Order().ToList();
    }

    private static int? Number(string cell)
    {
        var c = cell.Trim();
        if (c is "-" or "–" or "—") return 0;
        return Regex.Match(c, @"^\d+") is { Success: true } m ? int.Parse(m.Value) : null;
    }

    private static int? YearOf(string name, string head)
    {
        if (Regex.Match(name, @"20\d\d") is { Success: true } f) return int.Parse(f.Value);
        if (Regex.Match(head, @"Москва\W{0,5}(20\d\d)") is { Success: true } m) return int.Parse(m.Groups[1].Value);
        if (Regex.Match(head, @"(20\d\d)\s*г\.") is { Success: true } g) return int.Parse(g.Groups[1].Value);
        return null;
    }

    // Доля слов названия дисциплины (по основам — первые 5 букв), встретившихся в начале документа; точное вхождение — 1.
    private static double Score(string name, string where)
    {
        var text = Norm(where);
        if (text.Contains(Norm(name))) return 1.0 + name.Length / 1000.0;
        var words = Words(name);
        // Однословное название — только точным совпадением: основа «инфор» есть и в «информационных».
        if (words.Count < 2) return 0;
        var found = Words(where);
        var hit = words.Count(found.ToHashSet().Contains);
        return (double)hit / Math.Max(words.Count, found.Count);
    }

    private static List<string> Words(string s) => Regex.Matches(Norm(s), @"[а-яa-z]{3,}").Select(m => m.Value)
        .Where(w => w is not ("для" or "при" or "или" or "его" or "как"))
        .Select(w => w.Length > 5 ? w[..5] : w).Distinct().ToList();

    // Кавычки всех видов, тире, двоеточие и скобки, неразрывные пробелы — к одному виду: «Проект по “…”» и «Проект по "…"»,
    // «ПФК: Спортивные игры» и «ПФК (Спортивные игры)» — одно название.
    private static string Norm(string s)
    {
        var t = s.ToLowerInvariant().Replace('ё', 'е').Replace(' ', ' ');
        t = Regex.Replace(t, "[«»“”„‟\"'‘’‚:()]", " ");
        t = Regex.Replace(t, "[‐‑‒–—―]", "-");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"\b\d{2}\.0[3-5]\.\d{2}\b")] private static partial Regex DirectionCode();
    [GeneratedRegex(@"\b((?:УК|ОПК|ПК|ОК)-\d+)")] private static partial Regex CompetenceCode();
    [GeneratedRegex(@"\b(?:Б|ФТД)\d?\.[ОВ]\.(?:ДВ\.)?\d+(?:\.\d+)*", RegexOptions.IgnoreCase)] private static partial Regex PlanCode();
    [GeneratedRegex(@"курсов(?:ой|ого|ая|ую)\s+(?:проект|работ)", RegexOptions.IgnoreCase)] private static partial Regex CourseWork();
    [GeneratedRegex(@"общая\s+трудо[её]мк", RegexOptions.IgnoreCase)] private static partial Regex TotalRow();
    [GeneratedRegex(@"(\d+)\s*(?:-?\s*й)?\s*сем", RegexOptions.IgnoreCase)] private static partial Regex SemesterHeader();
    [GeneratedRegex(@"лекци", RegexOptions.IgnoreCase)] private static partial Regex Lectures();
    [GeneratedRegex(@"практическ|семинар", RegexOptions.IgnoreCase)] private static partial Regex Practice();
    [GeneratedRegex(@"лаборатор", RegexOptions.IgnoreCase)] private static partial Regex Labs();
    [GeneratedRegex(@"^самостоятельн", RegexOptions.IgnoreCase)] private static partial Regex SelfStudy();
    [GeneratedRegex(@"^(?:промежуточн\w*\s+)?контрол|^контроль", RegexOptions.IgnoreCase)] private static partial Regex Control();
    [GeneratedRegex(@"^контактн", RegexOptions.IgnoreCase)] private static partial Regex Contact();
    [GeneratedRegex(@"вид\w*\s+(?:промежуточн|итогов)|форм\w*\s+промежуточн", RegexOptions.IgnoreCase)] private static partial Regex Attestation();

    /// <summary>
    /// Наглядное сравнение (раздел C): наш DOCX и PDF без содержимого автора — рядом с оригиналом, в папке отчёта.
    /// </summary>
    public async Task<List<string>> VisualAsync(RpdReport report, string output)
    {
        var result = new List<string>();
        if (report.DocumentId is not Guid id || report.Source is null) return result;
        var folder = Path.Combine(output, "Наглядно", Path.GetFileNameWithoutExtension(report.File));
        Directory.CreateDirectory(folder);
        System.IO.File.Copy(Path.Combine(corpus, report.Source), Path.Combine(folder, "оригинал - " + Path.GetFileName(report.Source)), true);
        await using var scope = bench.Scope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var printing = sp.GetRequiredService<DocumentPrinting>();
        var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
        foreach (var format in new[] { GenerationFormats.Docx, GenerationFormats.Pdf })
        {
            var task = printing.Queue(document, format, bench.RunnerId);
            await db.SaveChangesAsync();
            await printing.RunAsync(task);
            await db.SaveChangesAsync();
            var file = await db.GeneratedFiles.SingleOrDefaultAsync(f => f.GenerationTaskId == task.Id);
            if (file is null)
            {
                result.Add($"{format}: не сформирован — {task.ErrorMessage}");
                continue;
            }
            var target = Path.Combine(folder, "наш - " + file.FileName);
            System.IO.File.Copy(Path.Combine(bench.StoragePath, file.StoragePath), target, true);
            result.Add(Path.GetRelativePath(output, target));
        }
        return result;
    }
}
