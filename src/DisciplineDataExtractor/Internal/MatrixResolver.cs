namespace DisciplineDataExtractor.Internal;

/// <summary>
/// Сопоставляет строки матрицы с дисциплинами плана. В матрице нет индексов, только названия,
/// и они расходятся с планом в мелочах («ИТ-продуктов» / «ИТ продуктов») и в формулировках.
/// Порядок: точное совпадение ключа названия → совпадение набора слов (с предупреждением) →
/// при нескольких кандидатах — уточнение по семестрам. Иначе — замечание, а строка с отметками возвращается
/// данными (<see cref="UnmatchedMatrixRow"/>) для ручной привязки.
/// </summary>
internal static class MatrixResolver
{
    public static (IReadOnlyList<MatrixLink> Links, IReadOnlyList<UnmatchedMatrixRow> Unmatched) Resolve(
        string file, IReadOnlyList<MatrixRow> rows, IReadOnlyList<Discipline> disciplines,
        IReadOnlyList<Competence> competences, IssueLog log)
    {
        if (rows.Count == 0) return ([], []);
        if (disciplines.Count == 0)
        {
            log.Error(file, null, null, "Матрица не сопоставлена с планом: дисциплины плана не прочитаны");
            return ([], []);
        }

        var byName = disciplines.ToLookup(d => TextRules.NameKey(d.Name));
        var byWords = disciplines.ToLookup(d => TextRules.NameTokensKey(d.Name));
        var knownCompetences = competences.Select(c => c.Code).ToHashSet();
        var knownIndicators = competences.SelectMany(c => c.Indicators).Select(i => i.Code).ToHashSet();
        var reported = new HashSet<string>();

        var links = new List<MatrixLink>();
        var unmatched = new List<UnmatchedMatrixRow>();
        var seen = new HashSet<MatrixLink>();
        var covered = new HashSet<string>();
        foreach (var row in rows)
        {
            var (discipline, candidates) = Match(file, row, byName, byWords, log);
            if (discipline is null)
            {
                // Строка без отметок ничего не теряет — данными не возвращается.
                if (row.Codes.Count > 0)
                    unmatched.Add(new UnmatchedMatrixRow(row.Sheet, row.Row, row.Name, row.Semesters,
                        row.Codes.Select(c => c.Indicator ?? c.Competence).Distinct().ToList(),
                        row.Codes.Select(c => c.Competence).Distinct().ToList(),
                        candidates.Select(c => c.Code).ToList()));
                continue;
            }
            covered.Add(discipline.Code);

            foreach (var code in row.Codes)
            {
                var key = code.Indicator ?? code.Competence;
                var known = code.Indicator is null ? knownCompetences.Contains(key) : knownIndicators.Contains(key);
                if (competences.Count > 0 && !known && reported.Add(key))
                    log.Warning(file, row.Sheet, null, $"{key} из матрицы нет в списке компетенций");

                var link = new MatrixLink(discipline.Code, code.Competence, code.Indicator);
                if (seen.Add(link)) links.Add(link);
            }
        }

        foreach (var d in disciplines.Where(d =>
                     d.Kind != EntryKind.Module && d.Part != PlanPart.ElectiveGroup && !covered.Contains(d.Code)))
            log.Warning(file, null, null, $"Дисциплины плана {d.Code} «{d.Name}» нет в матрице компетенций");

        return (links, unmatched);
    }

    // Найденная дисциплина или null и кандидаты неоднозначной строки (пусто — не найдена).
    private static (Discipline? Discipline, IReadOnlyList<Discipline> Candidates) Match(
        string file, MatrixRow row, ILookup<string, Discipline> byName, ILookup<string, Discipline> byWords, IssueLog log)
    {
        var candidates = byName[TextRules.NameKey(row.Name)].ToList();
        var byWordsOnly = false;
        if (candidates.Count == 0)
        {
            candidates = byWords[TextRules.NameTokensKey(row.Name)].ToList();
            byWordsOnly = candidates.Count > 0;
        }

        if (candidates.Count > 1)
        {
            var semesters = row.Semesters.Select(TextRules.SemesterOf).OfType<int>().ToHashSet();
            var bySemesters = semesters.Count > 0
                ? candidates.Where(d => semesters.SetEquals(d.Semesters.Select(s => s.Semester))).ToList()
                : candidates;
            // Семестры не помогли — кандидаты остаются все: из них методист и выберет.
            if (bySemesters.Count > 0) candidates = bySemesters;
        }

        if (candidates.Count == 1)
        {
            if (byWordsOnly)
                log.Warning(file, row.Sheet, row.Row,
                    $"«{row.Name}» сопоставлена с {candidates[0].Code} «{candidates[0].Name}» по совпадению слов");
            return (candidates[0], []);
        }

        // Связи строки теряются — это ошибка; строка без отметок ничего не теряет — предупреждение.
        var lost = row.Codes.Count > 0
            ? ": связи с " + string.Join(", ", row.Codes.Select(c => c.Indicator ?? c.Competence)) + " не загружены"
            : "";
        var message = candidates.Count == 0
            ? $"Дисциплина «{row.Name}» не найдена в учебном плане{lost}"
            : $"Дисциплина «{row.Name}» неоднозначна ({string.Join(", ", candidates.Select(c => c.Code))}){lost}";
        if (row.Codes.Count > 0) log.Error(file, row.Sheet, row.Row, message);
        else log.Warning(file, row.Sheet, row.Row, message);
        return (null, candidates);
    }
}
