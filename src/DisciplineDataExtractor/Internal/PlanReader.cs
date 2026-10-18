using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace DisciplineDataExtractor.Internal;

/// <summary>
/// Учебный план (.xlsx, выгрузка plx): «Титул» — сведения о плане, «Кафедры» — справочник кафедр,
/// «План» — дисциплины с часами по семестрам. Колонки ищутся по заголовкам, а не по буквам:
/// в разных выгрузках они сдвигаются.
/// </summary>
internal sealed partial class PlanReader(SourceFile file, IssueLog log)
{
    private const string TitleSheet = "Титул", DepartmentsSheet = "Кафедры", PlanSheet = "План";

    // Индекс по сегментам: Б1.О.01, Б1.В.ДВ.01.02, Б2.О.03(П), Б3.01(Д), ФТД.В.01.
    // Заголовок «ФТД.Факультативы» начинается так же, но индексом не является.
    [GeneratedRegex(@"^(Б\d|ФТД)(\.(О|В|O|B|ДВ|\d{1,3}(\([А-Яа-яЁё]+\))?))+$")]
    private static partial Regex DisciplineCode();

    [GeneratedRegex(@"^\d{2}\.\d{2}\.\d{2}$")]
    private static partial Regex DirectionCodeOnly();

    [GeneratedRegex(@"[«""“„](?<q>[^«»""“”„]+)[»""”“]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"семестр\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SemesterHeader();

    [GeneratedRegex(@"№\s*(?<n>[^\s,]+)")]
    private static partial Regex OrderNumber();

    [GeneratedRegex(@"\d{2}\.\d{2}\.\d{4}")]
    private static partial Regex Date();

    private string F => file.FileName;

    public (CurriculumPlanInfo Plan, IReadOnlyList<Discipline> Disciplines) Read()
    {
        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var departments = ReadDepartments(workbook);
        var plan = ReadTitle(workbook, departments);
        var disciplines = ReadDisciplines(workbook, departments);
        return (plan, disciplines);
    }

    // ---------- Титул ----------

    private CurriculumPlanInfo ReadTitle(IXLWorkbook workbook, IReadOnlyList<DepartmentInfo> departments)
    {
        if (!workbook.TryGetWorksheet(TitleSheet, out var ws))
        {
            log.Error(F, null, null, $"Нет листа «{TitleSheet}»: сведения о плане не прочитаны");
            return new CurriculumPlanInfo(null, null, null, EducationLevel.Unknown, StudyForm.Unknown,
                null, null, null, null, departments);
        }

        var cells = ws.CellsUsed().Where(c => ExcelCells.Text(c).Length > 0).ToList();
        IXLCell? Find(string fragment) =>
            cells.FirstOrDefault(c => ExcelCells.Text(c).Contains(fragment, StringComparison.OrdinalIgnoreCase));
        string ValueOf(IXLCell cell)
        {
            // Значение — после двоеточия в той же ячейке или в ячейке правее.
            var own = AfterColon(ExcelCells.Text(cell));
            return own.Length > 0 ? own : ExcelCells.RightOf(cell) is { } right ? ExcelCells.Text(right) : "";
        }

        // Направление и профиль: «…(направление подготовки): 09.04.04 "Программная инженерия"
        // Направленность (профиль) программы: "Технологии…"». У специалитета — «Специальность», «Специализация».
        string? code = null, name = null, profile = null;
        if ((Find("направление подготовки") ?? Find("специальность:")) is { } direction)
        {
            var text = ExcelCells.Text(direction);
            var m = TextRules.DirectionCode().Match(text);
            if (m.Success)
            {
                code = m.Value;
                var profileAt = IndexOfAny(text, m.Index + m.Length, "направленность", "профиль", "специализация");
                name = FirstQuoted(text, m.Index + m.Length, profileAt < 0 ? text.Length : profileAt);
                if (profileAt >= 0)
                    profile = FirstQuoted(text, profileAt, text.Length);
            }
        }
        code ??= cells.Select(ExcelCells.Text).FirstOrDefault(t => DirectionCodeOnly().IsMatch(t));
        if (profile is null && Find("специализация") is { } specialization)
            profile = FirstQuoted(ExcelCells.Text(specialization), 0, ExcelCells.Text(specialization).Length)
                      ?? NullIfEmpty(ValueOf(specialization));

        var level = Find("квалификация") is { } q ? LevelOf(AfterColon(ExcelCells.Text(q))) : EducationLevel.Unknown;
        if (level == EducationLevel.Unknown && Find("по программе") is { } program)
        {
            // «по программе магистратуры» — запасной источник уровня, если нет «Квалификация: …».
            var text = ExcelCells.Text(program);
            level = LevelOf(text[(text.IndexOf("по программе", StringComparison.OrdinalIgnoreCase) + "по программе".Length)..]);
        }

        var form = Find("форма обучения") is { } f ? FormOf(ValueOf(f)) : StudyForm.Unknown;

        int? year = null;
        if (Find("год начала подготовки") is { } y && ExcelCells.RightOf(y) is { } yv &&
            int.TryParse(ExcelCells.Text(yv), out var parsed) && parsed is >= 1990 and <= 2100)
            year = parsed;

        FgosInfo? fgos = null;
        if (Find("образовательный стандарт") is { } s && ExcelCells.RightOf(s) is { } sv)
        {
            var order = ExcelCells.Text(sv);
            var number = OrderNumber().Match(order);
            var date = Date().Match(order);
            fgos = new FgosInfo(order, number.Success ? number.Groups["n"].Value : null,
                date.Success ? DateOnly.ParseExact(date.Value, "dd.MM.yyyy", CultureInfo.InvariantCulture) : null);
        }

        var department = Find("кафедра:") is { } d ? NullIfEmpty(ValueOf(d)) : null;

        // Институт — отдельной строкой в шапке («…"СТАНКИН"\nИнститут информационных технологий»).
        var institute = cells
            .SelectMany(c => c.GetString().Split('\n'))
            .Select(TextRules.Clean)
            .FirstOrDefault(line => line.StartsWith("Институт", StringComparison.OrdinalIgnoreCase));

        if (code is null) log.Error(F, TitleSheet, null, "Не найден код направления подготовки");
        if (name is null) log.Warning(F, TitleSheet, null, "Не найдено наименование направления подготовки");
        if (profile is null) log.Warning(F, TitleSheet, null, "Не найдена направленность (профиль)");
        if (level == EducationLevel.Unknown) log.Error(F, TitleSheet, null, "Не определён уровень образования (квалификация)");
        if (form == StudyForm.Unknown) log.Error(F, TitleSheet, null, "Не определена форма обучения");
        if (year is null) log.Error(F, TitleSheet, null, "Не найден год начала подготовки");
        if (fgos is null) log.Warning(F, TitleSheet, null, "Не найден образовательный стандарт (ФГОС)");
        if (institute is null) log.Warning(F, TitleSheet, null, "Не найден институт");
        if (department is null) log.Warning(F, TitleSheet, null, "Не найдена выпускающая кафедра");

        return new CurriculumPlanInfo(code, name, profile, level, form, year, institute, department, fgos, departments);
    }

    private static EducationLevel LevelOf(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.StartsWith("магистр")) return EducationLevel.Master;
        if (t.StartsWith("бакалавр")) return EducationLevel.Bachelor;
        if (t.StartsWith("специалист") || t.StartsWith("специалитет")) return EducationLevel.Specialist;
        if (t.StartsWith("аспирант") || t.StartsWith("исследователь")) return EducationLevel.Postgraduate;
        return EducationLevel.Unknown;
    }

    private static StudyForm FormOf(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("очно-заочн")) return StudyForm.Evening;
        if (t.Contains("заочн")) return StudyForm.PartTime;
        if (t.Contains("очн")) return StudyForm.FullTime;
        return StudyForm.Unknown;
    }

    // ---------- Кафедры ----------

    private IReadOnlyList<DepartmentInfo> ReadDepartments(IXLWorkbook workbook)
    {
        if (!workbook.TryGetWorksheet(DepartmentsSheet, out var ws))
        {
            log.Warning(F, null, null, $"Нет листа «{DepartmentsSheet}»: справочник кафедр плана пуст");
            return [];
        }

        var header = ws.RowsUsed().Take(10).FirstOrDefault(r =>
            r.CellsUsed().Any(c => TextRules.HeaderKey(c.GetString()) is "номер" or "код"));
        if (header is null)
        {
            log.Error(F, DepartmentsSheet, null, "Не найдена строка заголовков (колонка «Номер»)");
            return [];
        }

        int? Column(Func<string, bool> match) =>
            header.CellsUsed().FirstOrDefault(c => match(TextRules.HeaderKey(c.GetString())))?.Address.ColumnNumber;
        var codeCol = Column(k => k is "номер" or "код");
        var shortCol = Column(k => k.StartsWith("аббревиатура") || k.StartsWith("сокращ"));
        var nameCol = Column(k => k.StartsWith("название") || k.StartsWith("наименование"));
        if (codeCol is null || nameCol is null)
        {
            log.Error(F, DepartmentsSheet, header.RowNumber(), "Нет колонок «Номер» и «Название кафедры»");
            return [];
        }

        var result = new List<DepartmentInfo>();
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > header.RowNumber()))
        {
            var code = ExcelCells.Text(row.Cell(codeCol.Value));
            var name = ExcelCells.Text(row.Cell(nameCol.Value));
            if (code.Length == 0 && name.Length == 0) continue;
            if (code.Length == 0 || name.Length == 0)
            {
                log.Warning(F, DepartmentsSheet, row.RowNumber(), "Кафедра без номера или названия — строка пропущена");
                continue;
            }
            var shortName = shortCol is null ? "" : ExcelCells.Text(row.Cell(shortCol.Value));
            result.Add(new DepartmentInfo(code, shortName, name));
        }
        return result;
    }

    // ---------- План ----------

    private sealed record SemesterColumns(
        int Semester, int? Credits, int? Lectures, int? Labs, int? Practice, int? SelfStudy, int? Control,
        int? Contact, IReadOnlyList<int> ExtraContact);

    private sealed record Layout(
        int HeaderRow, int Index, int Name, int? Credits, int? Hours, int? ContactTotal, int? DeptCode, int? DeptName,
        IReadOnlyList<(int Column, ControlType Type)> Controls, IReadOnlyList<SemesterColumns> Semesters);

    private sealed record Row(
        int Number, string Code, string Name, PlanBlock Block, PlanPart Part, string? DeptCode, string? DeptName,
        int Credits, int Hours, IReadOnlyList<SemesterDetails> Semesters);

    private IReadOnlyList<Discipline> ReadDisciplines(IXLWorkbook workbook, IReadOnlyList<DepartmentInfo> departments)
    {
        if (!workbook.TryGetWorksheet(PlanSheet, out var ws))
        {
            log.Error(F, null, null, $"Нет листа «{PlanSheet}»: дисциплины не прочитаны");
            return [];
        }

        var layout = DetectLayout(ws);
        if (layout is null) return [];

        var knownDepartments = departments.Select(d => d.Code).ToHashSet();
        var rows = new List<Row>();
        var codes = new HashSet<string>();
        foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > layout.HeaderRow))
        {
            var code = ExcelCells.Text(row.Cell(layout.Index));
            if (!DisciplineCode().IsMatch(code)) continue; // заголовки блоков и частей, итоги

            var number = row.RowNumber();
            var name = ExcelCells.Text(row.Cell(layout.Name));
            if (name.Length == 0)
            {
                log.Error(F, PlanSheet, number, $"{code}: нет наименования — строка пропущена");
                continue;
            }
            if (!codes.Add(code))
            {
                log.Error(F, PlanSheet, number, $"{code}: индекс повторяется — строка пропущена");
                continue;
            }
            var classified = Classify(code, number);
            if (classified is null) continue;
            var (block, part) = classified.Value;

            var deptCode = layout.DeptCode is int dc ? NullIfEmpty(ExcelCells.Text(row.Cell(dc))) : null;
            var deptName = layout.DeptName is int dn ? NullIfEmpty(ExcelCells.Text(row.Cell(dn))) : null;
            if (deptCode is not null && knownDepartments.Count > 0 && !knownDepartments.Contains(deptCode))
                log.Warning(F, PlanSheet, number, $"{code}: кафедра с номером {deptCode} отсутствует на листе «{DepartmentsSheet}»");

            var credits = layout.Credits is int cc ? ExcelCells.Int(row.Cell(cc), log, F, $"{code}: з.е.") : 0;
            var hours = layout.Hours is int hc ? ExcelCells.Int(row.Cell(hc), log, F, $"{code}: часы по плану") : 0;
            var semesters = ReadSemesters(row, layout, code);

            var semesterCredits = semesters.Sum(s => s.Credits);
            if (credits > 0 && semesterCredits > 0 && semesterCredits != credits)
                log.Warning(F, PlanSheet, number, $"{code}: сумма з.е. по семестрам ({semesterCredits}) не равна итогу ({credits})");
            if (layout.ContactTotal is int ct &&
                ExcelCells.Int(row.Cell(ct), log, F, $"{code}: контактная работа") is var contact && contact > 0 &&
                semesters.Sum(s => s.ContactHours) is var semesterContact && semesterContact != contact)
                log.Warning(F, PlanSheet, number,
                    $"{code}: контактные часы по семестрам ({semesterContact}) не равны итогу ({contact})");

            rows.Add(new Row(number, code, name, block, part, deptCode, deptName, credits, hours, semesters));
        }

        // Родитель — ближайший индекс-префикс: Б1.О.14.01 → Б1.О.14 (модуль), Б1.В.ДВ.01.02 → Б1.В.ДВ.01 (группа).
        var parents = rows.ToDictionary(r => r.Code, r => ParentOf(r.Code, codes));
        var withChildren = parents.Values.OfType<string>().ToHashSet();

        return rows.Select(r => new Discipline(
            r.Code, r.Name, r.Block, r.Part, KindOf(r, withChildren.Contains(r.Code)), parents[r.Code],
            r.DeptCode, r.DeptName, r.Credits, r.Hours, r.Semesters)).ToList();
    }

    private Layout? DetectLayout(IXLWorksheet ws)
    {
        var header = ws.RowsUsed().Take(15).FirstOrDefault(r =>
            r.CellsUsed().Any(c => TextRules.HeaderKey(c.GetString()) == "индекс"));
        if (header is null || header.RowNumber() < 3)
        {
            log.Error(F, PlanSheet, null, "Не найдена строка заголовков с колонкой «Индекс»");
            return null;
        }

        var h = header.RowNumber();
        // Над строкой заголовков — группы колонок: строка h-2 («Форма контроля», «з.е.», «Итого акад.часов»,
        // «Закрепленная кафедра»), строка h-1 — семестры. Границы групп — по объединённым ячейкам.
        var groups = ws.Row(h - 2).CellsUsed()
            .Select(c => (Span: ExcelCells.Span(c), Key: TextRules.HeaderKey(c.GetString()))).ToList();
        string GroupOf(int col) => groups.FirstOrDefault(g => col >= g.Span.First && col <= g.Span.Last).Key ?? "";
        var semesterSpans = SemesterSpans(ws.Row(h - 1), groups.Select(g => g.Span).ToList());
        int? SemesterOf(int col) =>
            semesterSpans.Where(s => col >= s.First && col <= s.Last).Select(s => (int?)s.Semester).FirstOrDefault();

        int? index = null, name = null, credits = null, creditsExpert = null, hours = null, hoursExpert = null,
            contact = null, deptCode = null, deptName = null;
        var controls = new List<(int, ControlType)>();
        var semesterColumns = new Dictionary<int, Dictionary<string, int>>();
        var extraContact = new Dictionary<int, List<int>>();

        foreach (var cell in header.CellsUsed())
        {
            var col = cell.Address.ColumnNumber;
            var sub = TextRules.HeaderKey(cell.GetString());
            var group = GroupOf(col);

            if (SemesterOf(col) is int semester)
            {
                if (sub is "кср" or "икр")
                    (extraContact.TryGetValue(semester, out var list) ? list : (extraContact[semester] = [])).Add(col);
                else
                    (semesterColumns.TryGetValue(semester, out var map) ? map : (semesterColumns[semester] = []))
                        .TryAdd(sub, col);
                continue;
            }

            if (sub == "индекс") index ??= col;
            else if (sub == "наименование" && group != "закрепленнаякафедра") name ??= col;
            else if (ControlOf(sub) is ControlType control) controls.Add((col, control));
            else if (group == "з.е." && sub == "факт") credits ??= col;
            else if (group == "з.е." && sub == "экспертное") creditsExpert ??= col;
            else if (group.StartsWith("итогоакад") && sub == "поплану") hours ??= col;
            else if (group.StartsWith("итогоакад") && sub == "экспертное") hoursExpert ??= col;
            else if (group.StartsWith("итогоакад") && sub == "конт.раб.") contact ??= col;
            else if (group == "закрепленнаякафедра" && sub == "код") deptCode ??= col;
            else if (group == "закрепленнаякафедра" && sub == "наименование") deptName ??= col;
        }

        if (index is null || name is null)
        {
            log.Error(F, PlanSheet, h, "Нет колонок «Индекс» и «Наименование»");
            return null;
        }
        if (semesterColumns.Count == 0)
            log.Error(F, PlanSheet, h - 1, "Не найдены колонки семестров («Семестр N»): часы по семестрам не прочитаны");
        if (controls.Count == 0)
            log.Error(F, PlanSheet, h, "Не найдены колонки формы контроля: виды аттестации не прочитаны");
        if ((credits ?? creditsExpert) is null)
            log.Warning(F, PlanSheet, h, "Не найдена колонка з.е. («Факт» или «Экспертное»)");
        if (deptCode is null)
            log.Warning(F, PlanSheet, h, "Не найдена колонка закреплённой кафедры");

        var semesters = semesterColumns.OrderBy(s => s.Key).Select(s =>
        {
            int? C(string key) => s.Value.TryGetValue(key, out var c) ? c : null;
            return new SemesterColumns(s.Key, C("з.е."), C("лек"), C("лаб"), C("пр"), C("ср"), C("контроль"),
                C("конт.раб."), extraContact.TryGetValue(s.Key, out var extra) ? extra : []);
        }).ToList();

        return new Layout(h, index.Value, name.Value, credits ?? creditsExpert, hours ?? hoursExpert, contact,
            deptCode, deptName, controls, semesters);
    }

    /// <summary>
    /// Колонки семестров. Если «Семестр N» не объединён с соседними ячейками, группа тянется
    /// до следующего семестра или до конца группы верхней строки.
    /// </summary>
    private static List<(int First, int Last, int Semester)> SemesterSpans(
        IXLRow row, IReadOnlyList<(int First, int Last)> topGroups)
    {
        var headers = row.CellsUsed()
            .Select(c => (Cell: c, Match: SemesterHeader().Match(c.GetString())))
            .Where(x => x.Match.Success)
            .Select(x => (x.Cell, Semester: int.Parse(x.Match.Groups["n"].Value, CultureInfo.InvariantCulture)))
            .OrderBy(x => x.Cell.Address.ColumnNumber)
            .ToList();

        var spans = new List<(int, int, int)>();
        for (var i = 0; i < headers.Count; i++)
        {
            var (first, last) = ExcelCells.Span(headers[i].Cell);
            if (first == last)
            {
                var next = i + 1 < headers.Count ? headers[i + 1].Cell.Address.ColumnNumber - 1 : int.MaxValue;
                var top = topGroups.FirstOrDefault(g => first >= g.First && first <= g.Last);
                last = Math.Min(next, top == default ? next : top.Last);
            }
            spans.Add((first, last, headers[i].Semester));
        }
        return spans;
    }

    private static ControlType? ControlOf(string headerKey) => headerKey switch
    {
        "экзамен" => ControlType.Exam,
        "зачет" => ControlType.Credit,
        _ when headerKey.StartsWith("зачетсоц") => ControlType.GradedCredit,
        "кп" => ControlType.CourseworkProject,
        "кр" => ControlType.CourseworkPaper,
        _ => null
    };

    private IReadOnlyList<SemesterDetails> ReadSemesters(IXLRow row, Layout layout, string code)
    {
        var number = row.RowNumber();

        // Форма контроля: в ячейке — номера семестров подряд («12» — в 1-м и 2-м, «A» — в 10-м).
        var controls = new Dictionary<int, List<ControlType>>();
        foreach (var (col, type) in layout.Controls)
        {
            foreach (var ch in ExcelCells.Text(row.Cell(col)))
            {
                if (ch is ' ' or ',' or ';') continue;
                if (TextRules.SemesterOf(ch) is not int semester)
                {
                    log.Error(F, PlanSheet, number, $"{code}: неизвестное обозначение семестра «{ch}» в форме контроля");
                    continue;
                }
                (controls.TryGetValue(semester, out var list) ? list : (controls[semester] = [])).Add(type);
            }
        }

        var result = new List<SemesterDetails>();
        foreach (var s in layout.Semesters)
        {
            int V(int? col, string what) =>
                col is int c ? ExcelCells.Int(row.Cell(c), log, F, $"{code}, семестр {s.Semester}: {what}") : 0;

            var credits = V(s.Credits, "з.е.");
            var lectures = V(s.Lectures, "лекции");
            var labs = V(s.Labs, "лабораторные");
            var practice = V(s.Practice, "практические");
            var selfStudy = V(s.SelfStudy, "самостоятельная работа");
            var control = V(s.Control, "контроль");
            // Своей колонки контактной работы у семестра обычно нет: это аудиторные часы (и КСР, ИКР, если есть).
            var contact = s.Contact is not null
                ? V(s.Contact, "контактная работа")
                : lectures + labs + practice + s.ExtraContact.Sum(c => V(c, "контактная работа"));
            controls.Remove(s.Semester, out var types);

            if (credits + lectures + labs + practice + selfStudy + control + contact == 0 && types is null) continue;
            result.Add(new SemesterDetails(s.Semester, credits, lectures, labs, practice, selfStudy, control, contact,
                (types ?? []).Distinct().ToList()));
        }

        foreach (var (semester, types) in controls.OrderBy(c => c.Key))
        {
            log.Warning(F, PlanSheet, number, $"{code}: аттестация в семестре {semester}, для которого нет колонок часов");
            result.Add(new SemesterDetails(semester, 0, 0, 0, 0, 0, 0, 0, types.Distinct().ToList()));
        }
        return result.OrderBy(d => d.Semester).ToList();
    }

    private (PlanBlock, PlanPart)? Classify(string code, int row)
    {
        var segments = code.Split('.');
        PlanBlock? block = segments[0] switch
        {
            "Б1" => PlanBlock.B1,
            "Б2" => PlanBlock.B2,
            "Б3" => PlanBlock.B3,
            "ФТД" => PlanBlock.Ftd,
            _ => null
        };
        if (block is null)
        {
            log.Error(F, PlanSheet, row, $"{code}: неизвестный блок плана — строка пропущена");
            return null;
        }

        var dv = Array.FindIndex(segments, s => s is "ДВ" or "DV");
        if (dv >= 0)
            return (block.Value, segments.Length - dv - 1 <= 1 ? PlanPart.ElectiveGroup : PlanPart.ElectiveItem);

        // Вторая часть индекса: «О» — обязательная, «В» — формируемая участниками (бывает и латиницей).
        var part = segments.Length > 1 ? segments[1] : "";
        if (part is "О" or "O") return (block.Value, PlanPart.Mandatory);
        if (part is "В" or "B") return (block.Value, PlanPart.Variable);
        if (block == PlanBlock.B3) return (block.Value, PlanPart.Mandatory); // Б3.01(Д): ГИА — обязательная часть

        log.Error(F, PlanSheet, row, $"{code}: не удалось определить часть плана по индексу — строка пропущена");
        return null;
    }

    private static string? ParentOf(string code, IReadOnlySet<string> codes)
    {
        var candidate = code;
        while (candidate.LastIndexOf('.') is var dot && dot > 0)
        {
            candidate = candidate[..dot];
            if (codes.Contains(candidate)) return candidate;
        }
        return null;
    }

    private static EntryKind KindOf(Row row, bool hasChildren) => row.Block switch
    {
        PlanBlock.B2 => EntryKind.Practice,
        PlanBlock.B3 => EntryKind.Gia,
        PlanBlock.Ftd => EntryKind.Optional,
        _ => hasChildren && row.Part != PlanPart.ElectiveGroup ? EntryKind.Module : EntryKind.Discipline
    };

    // ---------- Помощники ----------

    private static string AfterColon(string text)
    {
        var colon = text.IndexOf(':');
        return colon >= 0 ? text[(colon + 1)..].Trim() : "";
    }

    private static int IndexOfAny(string text, int from, params string[] fragments) =>
        fragments.Select(f => text.IndexOf(f, from, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0).DefaultIfEmpty(-1).Min();

    private static string? FirstQuoted(string text, int from, int to)
    {
        var m = Quoted().Match(text[from..to]);
        return m.Success ? TextRules.Clean(m.Groups["q"].Value) : null;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
