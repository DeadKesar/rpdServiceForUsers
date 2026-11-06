using System.Diagnostics;
using System.Text.Json;
using DisciplineDataExtractor;
using Discipline = RpdFos.Core.Data.Entities.Discipline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Import;
using RpdFos.Core.Printing;
using Parsed = DisciplineDataExtractor;

namespace RpdFos.Corpus;

/// <summary>Строка таблицы дисциплин в отчёте по плану.</summary>
public sealed record DisciplineRow(
    string Code, string Name, string Semesters, int Credits, int Hours, string Attestation, int Labs, int Practice, int Ksr,
    int Competences, string Document, int PlanIssues, string Print);

/// <summary>Итог прогона комплекта: что разобрано, импортировано, найдено.</summary>
public sealed class PlanReport(CorpusSet set)
{
    public CorpusSet Set { get; } = set;
    public string? Matrix { get; set; }

    /// <summary>Профиль, подставленный инструментом, когда разбор его не нашёл; null — распознан.</summary>
    public string? SubstitutedProfile { get; set; }
    public List<string> MatrixChoice { get; } = [];
    public ExtractionResult? Parsed { get; set; }
    public string Status { get; set; } = "не запускался";
    public Guid? PlanId { get; set; }
    public List<DisciplineRow> Disciplines { get; } = [];
    public List<Finding> Findings { get; } = [];
    public List<string> UnmatchedRows { get; } = [];
    public List<string> WithoutCompetences { get; } = [];
    public Dictionary<string, int> PrintWarnings { get; } = [];
    public TimeSpan Elapsed { get; set; }
    public bool HasPlan => PlanId is not null;
}

/// <summary>
/// Прогон комплекта (раздел B): разбор — библиотекой, импорт — конвейером системы, по каждой дисциплине плана — новая
/// РПД (предзаполнение), полная проверка и печать DOCX по оформлению v3 без содержимого автора. В находки попадает то,
/// что возникает из данных плана, а не из незаполненных полей автора.
/// </summary>
public sealed class PlanRunner(Workbench bench, string corpus)
{
    public async Task<PlanReport> RunAsync(CorpusSet set)
    {
        var report = new PlanReport(set);
        var watch = Stopwatch.StartNew();
        try
        {
            await RunCoreAsync(report);
        }
        catch (Exception e)
        {
            // Сбой на одном комплекте не останавливает прогон: он — находка, остальные идут дальше.
            report.Status = "сбой прогона";
            report.Findings.Add(new(set.Name, "сбой", "", e.GetBaseException().Message, FindingClasses.ParseError));
        }
        report.Elapsed = watch.Elapsed;
        return report;
    }

    private async Task RunCoreAsync(PlanReport report)
    {
        var set = report.Set;
        var plan = Source(set.Plan);
        var list = Source(set.CompetenceList);
        report.Matrix = set.Matrix ?? ChooseMatrix(report, plan, list);
        if (report.Matrix is null)
        {
            report.Status = "нет матрицы";
            report.Findings.Add(new(set.Name, "комплект", "", "Матрица не выбрана и кандидатов не найдено", FindingClasses.SourceData));
            return;
        }
        var matrix = Source(report.Matrix);
        var parsed = new DataExtractor().Extract(new ExtractionInput(plan, list, matrix));
        Recognition(report, parsed);
        foreach (var issue in parsed.Issues) report.Findings.Add(ParseFinding(report, issue));
        // Профиль не распознан — без него план не создаётся, и весь раздел B для комплекта пропал бы. Это находка
        // (записана выше); чтобы увидеть следующие, инструмент подставляет профиль из комплекта или имя файла.
        // Систему это не меняет: подстановка — только во входных данных прогона.
        if (parsed.Plan is { Profile: null } info)
        {
            report.SubstitutedProfile = set.Profile.Length > 0 ? set.Profile : set.Name;
            parsed = parsed with { Plan = info with { Profile = report.SubstitutedProfile } };
        }
        report.Parsed = parsed;

        // Импорт — тем же конвейером, что фоновая задача хоста.
        await using var scope = bench.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var files = new[] { (plan, UploadedFileTypes.CurriculumPlan), (list, UploadedFileTypes.CompetenceReference),
            (matrix, UploadedFileTypes.CompetenceMatrix) }
            .Select(f => Workbench.AddFile(db, bench.RunnerId, f.Item1.FileName, f.Item1.Content, f.Item2)).ToList();
        var run = new ParserRun
        {
            StartedBy = bench.RunnerId, PlanFileId = files[0].Id, CompListFileId = files[1].Id, CompMatrixFileId = files[2].Id,
            StartedAt = DateTimeOffset.UtcNow
        };
        db.ParserRuns.Add(run);
        await db.SaveChangesAsync();
        run = await scope.ServiceProvider.GetRequiredService<ReferenceImporter>().ImportAsync(run.Id, parsed);
        report.Status = run.Status + (report.SubstitutedProfile is null ? "" : ", профиль подставлен");
        report.PlanId = run.CreatedCurriculumPlanId;
        // Замечания самого конвейера (кафедры, институты, компетенции) — те, которых нет в разборе.
        var parseMessages = parsed.Issues.Select(i => i.Message).ToHashSet();
        // «Тип не распознан — не загружен» повторяет замечание разбора об индикаторе — без двойного счёта.
        foreach (var issue in ImportIssue.Deserialize(run.ErrorsLog)
                     .Where(i => !parseMessages.Contains(i.Message) && !i.Message.Contains("тип не распознан — не загружен")))
            report.Findings.Add(ImportFinding(report, issue));
        if (report.PlanId is not Guid planId) return;

        report.UnmatchedRows.AddRange(await db.MatrixUnmatchedRows.Where(r => r.CurriculumPlanId == planId)
            .OrderBy(r => r.Sheet).ThenBy(r => r.RowNumber)
            .Select(r => r.Name + " (" + r.Sheet + ", строка " + r.RowNumber + "): " + string.Join(", ", r.Codes)).ToListAsync());
        await DisciplinesAsync(report, planId);
    }

    // Матрица не выбрана: прогнать разбор с каждым кандидатом и взять ту, у которой меньше несопоставленных строк.
    private string? ChooseMatrix(PlanReport report, SourceFile plan, SourceFile list)
    {
        (string Path, int Unmatched)? best = null;
        foreach (var name in report.Set.MatrixCandidates)
        {
            var path = CorpusSet.FindMatrix(corpus, name);
            if (path is null)
            {
                report.MatrixChoice.Add($"«{name}» — файл не найден");
                continue;
            }
            var parsed = new DataExtractor().Extract(new ExtractionInput(plan, list, Source(path)));
            var unmatched = parsed.Issues.Count(i => i.File == System.IO.Path.GetFileName(path) && IsUnmatchedRow(i.Message));
            report.MatrixChoice.Add($"«{name}» — несопоставленных строк: {unmatched}");
            if (best is null || unmatched < best.Value.Unmatched) best = (path, unmatched);
        }
        if (best is { } chosen) report.MatrixChoice.Add($"Выбрана «{System.IO.Path.GetFileName(chosen.Path)}»");
        return best?.Path;
    }

    private static bool IsUnmatchedRow(string message) => message.Contains("не найдена в учебном плане") || message.Contains("неоднозначна");

    private void Recognition(PlanReport report, ExtractionResult parsed)
    {
        var p = parsed.Plan;
        void Missing(string what) =>
            report.Findings.Add(new(report.Set.Name, "титул плана", "", $"Не распознано: {what}", FindingClasses.ParseError));
        if (p?.DirectionCode is null) Missing("код направления");
        if (p?.Profile is null) Missing("профиль");
        if (p is null || p.Level == EducationLevel.Unknown) Missing("уровень образования");
        if (p is null || p.StudyForm == Parsed.StudyForm.Unknown) Missing("форма обучения");
        if (p?.Year is null) Missing("год набора");
        if (p?.Year is int year && report.Set.Year is int expected && year != expected)
            report.Findings.Add(new(report.Set.Name, "титул плана", "", $"Год набора {year}, в комплекте {expected}", FindingClasses.ParseError));
    }

    // Класс замечания разбора — по тексту: правила уточнены по первому прогону.
    private static Finding ParseFinding(PlanReport report, ParseIssue issue)
    {
        var place = $"{issue.File}{(issue.Sheet is null ? "" : $", {issue.Sheet}")}{(issue.Row is null ? "" : $", строка {issue.Row}")}";
        var m = issue.Message;
        var kind = issue.Severity == IssueSeverity.Error ? "ошибка разбора" : "предупреждение разбора";
        var cls = m switch
        {
            _ when m.StartsWith("Файл не прочитан") => FindingClasses.ParseError,
            _ when IsUnmatchedRow(m) || m.Contains("нет в матрице компетенций") || m.Contains("нет в списке компетенций")
                   || m.Contains("не совпадает с направлением") => FindingClasses.SourceData,
            _ when m.Contains("по совпадению слов") => FindingClasses.Expected,
            // Почасовая разбивка бакалаврских планов — на листах «Курс N», разбор её не читает: нули — ошибка разбора.
            _ when m.Contains("контактные часы по семестрам") => FindingClasses.ParseError,
            // Название направления и профиль в титуле есть, но без кода в ячейке — разбор их не находит.
            _ when m.StartsWith("Не найдено наименование направления") || m.StartsWith("Не найдена направленность") => FindingClasses.ParseError,
            // Индикаторы без «Знать/Уметь/Владеть» модель не принимает — пробел модели, а не данных.
            _ when m.Contains("не распознан") && m.Contains("знать") => FindingClasses.ModelGap,
            _ => issue.Severity == IssueSeverity.Error ? FindingClasses.ParseError : FindingClasses.SourceData
        };
        if (cls == FindingClasses.SourceData && report.Set.OlderMatrix && issue.File == System.IO.Path.GetFileName(report.Matrix))
            m += " (матрица другого года)";
        return new(report.Set.Name, kind, place, m, cls);
    }

    private static Finding ImportFinding(PlanReport report, ImportIssue issue)
    {
        // Сокращение института — из файла должностей, а прогон его не загружает (как и «Импорт данных» с этапа 5.2).
        var cls = issue.Message.Contains("сокращение неизвестно") ? FindingClasses.Expected
            : issue.Message.Contains("не загружена — дисциплина без кафедры") || issue.Message.Contains("уже у кафедры")
            ? FindingClasses.SourceData
            : issue.Severity == ImportIssue.Error ? FindingClasses.ParseError : FindingClasses.SourceData;
        return new(report.Set.Name, "импорт", issue.File, issue.Message, cls);
    }

    // По каждой настоящей дисциплине плана — новая РПД, полная проверка, печать DOCX.
    private async Task DisciplinesAsync(PlanReport report, Guid planId)
    {
        var templateId = await bench.PublishTemplateAsync(planId);
        List<Discipline> disciplines;
        await using (var scope = bench.Scope())
            disciplines = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Disciplines.AsNoTracking()
                .Include(d => d.Semesters).Where(d => d.CurriculumPlanId == planId).OrderBy(d => d.Code).ToListAsync();

        foreach (var d in disciplines)
        {
            var subject = $"{report.Set.Name} / {d.Code}";
            if (d.TotalCredits == 0 && d.TotalHours > 0)
                report.Findings.Add(new(subject, "0 з. е.", d.Code, $"«{d.Name}»: {d.TotalHours} ч при 0 з. е.", FindingClasses.ModelGap));
            if (!DocumentRules.CanHaveDocument(d)) continue;

            var row = await DisciplineAsync(report, d, templateId, subject);
            report.Disciplines.Add(row);
        }

        // Дисциплины без связей с матрицей: новая РПД получит пустой список компетенций.
        report.WithoutCompetences.AddRange(report.Disciplines.Where(r => r.Competences == 0).Select(r => $"{r.Code} {r.Name}"));
    }

    private async Task<DisciplineRow> DisciplineAsync(PlanReport report, Discipline d, Guid templateId, string subject)
    {
        var semesters = d.Semesters.OrderBy(s => s.Semester).ToList();
        var attestation = string.Join("; ", semesters.Select(s => $"{s.Semester}: {string.Join(", ", s.ControlTypes.Select(PlanTitles.ControlType))}"));
        var ksr = semesters.Sum(s => Math.Max(0, s.ContactHours - s.Lectures - s.Practice - s.LabHours));

        if (semesters.Count == 0)
            report.Findings.Add(new(subject, "семестры", d.Code, $"«{d.Name}»: нет семестров с часами", FindingClasses.ParseError));
        if (semesters.Count > 0 && semesters.All(s => s.ControlTypes.Count == 0))
            report.Findings.Add(new(subject, "аттестация", d.Code, $"«{d.Name}»: ни в одном семестре нет вида аттестации", FindingClasses.ParseError));
        if (semesters.Count > 1)
            report.Findings.Add(new(subject, "несколько семестров", d.Code,
                $"«{d.Name}»: семестры {PlanTitles.Semesters(semesters.Select(s => s.Semester))}, аттестация {attestation}", FindingClasses.Expected));
        if (ksr > 0)
            report.Findings.Add(new(subject, "КСР и ИКР", d.Code,
                $"«{d.Name}»: контактной работы сверх занятий {ksr} ч", FindingClasses.Expected));
        if (semesters.Any(s => s.ControlTypes.Any(t => t is ControlTypeCodes.CourseProject or ControlTypeCodes.CourseWork)))
            report.Findings.Add(new(subject, "курсовое проектирование", d.Code,
                $"«{d.Name}»: {string.Join(", ", semesters.SelectMany(s => s.ControlTypes).Where(t => t is ControlTypeCodes.CourseProject or ControlTypeCodes.CourseWork).Distinct().Select(PlanTitles.ControlType))}",
                FindingClasses.Expected));

        await using var scope = bench.Scope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var created = await sp.GetRequiredService<DocumentCreation>().CreateAsync(templateId, d.Id, bench.RunnerId);
        if (created.Document is not { } document)
        {
            report.Findings.Add(new(subject, "создание РПД", d.Code, $"«{d.Name}»: {created.Problem} {created.Detail}", FindingClasses.ModelGap));
            return new(d.Code, d.Name, PlanTitles.Semesters(semesters.Select(s => s.Semester)), d.TotalCredits, d.TotalHours, attestation,
                semesters.Sum(s => s.LabHours), semesters.Sum(s => s.Practice), ksr, 0, "не создан", 0, "—");
        }

        var facts = (await DisciplineFacts.LoadAsync(db, d.Id))!;
        var schemaJson = await db.DocumentTemplates.Where(t => t.Id == templateId).Select(t => t.Schema).SingleAsync();
        var registry = sp.GetRequiredService<DocumentTypeRegistry>();
        var payload = JsonDocument.Parse(document.Payload).RootElement;
        var issues = DocumentSubmission.Check(schemaJson, payload, facts, sp.GetRequiredService<IDocumentValidator>(),
            sp.GetRequiredService<ValidationRuleSet>(), registry.FindSchema);
        // Часы по семестрам не прочитаны (всё по нулям при ненулевом итоге) — расхождение с з. е. от разбора, не от плана.
        var hoursUnread = d.TotalHours > 0 && semesters.All(s => s.Lectures + s.Practice + s.LabHours + s.SelfStudy == 0);
        var planIssues = PlanIssues(issues, JsonDocument.Parse(schemaJson).RootElement, registry, hoursUnread).ToList();
        foreach (var i in planIssues)
            report.Findings.Add(new(subject, i.Kind, i.Path, $"«{d.Name}»: {i.Message}", i.Class));

        // Печать DOCX нового документа без содержимого автора — тем же сервисом, что фоновая задача.
        var printing = sp.GetRequiredService<DocumentPrinting>();
        var task = printing.Queue(document, GenerationFormats.Docx, bench.RunnerId);
        await db.SaveChangesAsync();
        string print;
        try
        {
            await printing.RunAsync(task);
            await db.SaveChangesAsync();
            var printReport = DocumentPrinting.ReadReport(task.Report);
            foreach (var w in printReport.Warnings)
                report.PrintWarnings[w] = report.PrintWarnings.GetValueOrDefault(w) + 1;
            print = task.Status == GenerationStatuses.Succeeded
                ? $"сформирован, незаполненных мест {printReport.Unfilled.Count}"
                : $"ошибка: {task.ErrorMessage}";
            if (task.Status != GenerationStatuses.Succeeded)
                report.Findings.Add(new(subject, "печать", d.Code, $"«{d.Name}»: {task.ErrorMessage}", FindingClasses.Print));
        }
        catch (Exception e)
        {
            print = $"сбой: {e.GetBaseException().Message}";
            report.Findings.Add(new(subject, "печать", d.Code, $"«{d.Name}»: {print}", FindingClasses.Print));
        }

        return new(d.Code, d.Name, PlanTitles.Semesters(semesters.Select(s => s.Semester)), d.TotalCredits, d.TotalHours, attestation,
            semesters.Sum(s => s.LabHours), semesters.Sum(s => s.Practice), ksr, facts.Competences.Count, "создан", planIssues.Count, print);
    }

    private sealed record PlanIssue(string Kind, string Path, string Message, string Class);

    /// <summary>
    /// Замечания полной проверки, которые возникают из данных плана: правило о сумме часов семестра и ошибки в полях
    /// из плана (титул, трудоёмкость, вид курсового проектирования). Ошибки незаполненных полей автора — не находки.
    /// </summary>
    private static IEnumerable<PlanIssue> PlanIssues(IReadOnlyList<DocumentValidationError> issues, JsonElement schema, DocumentTypeRegistry registry,
        bool hoursUnread)
    {
        var planPaths = PlanFields.Paths(schema, registry.FindSchema).Concat(PlanFields.DerivedPaths(schema, registry.FindSchema)).ToList();
        bool FromPlan(string path) => planPaths.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
        foreach (var i in issues)
        {
            if (i.Path.StartsWith("/hours_distribution/semesters/", StringComparison.Ordinal) && i.Message.Contains("з. е. ×"))
                yield return new("hours_match_curriculum", i.Path, i.Message,
                    i.Message.Contains(" 0 з. е.") ? FindingClasses.ModelGap
                    : hoursUnread ? FindingClasses.ParseError : FindingClasses.SourceData);
            // Пустая кафедра — следствие незагруженной кафедры (её номера нет в файле должностей или он занят).
            else if (i.Path == "/title_page/department_name")
                yield return new("поле из плана", i.Path, i.Message, FindingClasses.SourceData);
            else if (FromPlan(i.Path) && !i.Message.StartsWith(PlanFields.DiffersFromPlan))
                yield return new("поле из плана", i.Path, i.Message, FindingClasses.ParseError);
        }
    }

    private SourceFile Source(string relative)
    {
        var path = Path.Combine(corpus, relative.Replace('/', Path.DirectorySeparatorChar));
        return new SourceFile(Path.GetFileName(path), File.ReadAllBytes(path));
    }
}
