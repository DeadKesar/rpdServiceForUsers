using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DisciplineDataExtractor.Internal;

/// <summary>
/// Список компетенций (.docx): таблицы УК (категория — компетенция — индикаторы), ОПК и ПК
/// (компетенция — индикаторы). Строка таблицы читается по абзацам: абзац с кодом компетенции
/// начинает компетенцию, с кодом индикатора — индикатор, абзац без кода продолжает предыдущий.
/// </summary>
internal sealed class CompetenceListReader(SourceFile file, IssueLog log)
{
    private sealed class CompetenceDraft(CompetenceCode code, string? categoryName, string formulation)
    {
        public CompetenceCode Code { get; } = code;
        public string? CategoryName { get; } = categoryName;
        public string Formulation { get; set; } = formulation;
        public List<IndicatorDraft> Indicators { get; } = [];
    }

    private sealed class IndicatorDraft(CompetenceCode code, string text, string sheet, int row)
    {
        public CompetenceCode Code { get; } = code;
        public string Text { get; set; } = text;
        public string Sheet { get; } = sheet;
        public int Row { get; } = row;
    }

    private string F => file.FileName;

    public (string? DirectionCode, IReadOnlyList<Competence> Competences) Read()
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        if (WordTables.BodyOf(doc) is not { } body)
        {
            log.Error(F, null, null, "В документе нет содержимого");
            return (null, []);
        }

        var direction = TextRules.DirectionCode().Match(WordTables.TextOf(body));
        var competences = new List<CompetenceDraft>();
        var byCode = new Dictionary<string, CompetenceDraft>();
        var indicators = new List<IndicatorDraft>();

        var tables = WordTables.Tables(body);
        for (var t = 0; t < tables.Count; t++)
        {
            var sheet = $"Таблица {t + 1}";
            var rows = tables[t].Elements<TableRow>().ToList();
            for (var r = 0; r < rows.Count; r++)
            {
                string? category = null;
                foreach (var cell in rows[r].Elements<TableCell>())
                {
                    CompetenceDraft? competence = null;
                    IndicatorDraft? indicator = null;
                    var startedWithCode = false;

                    foreach (var paragraph in WordTables.Paragraphs(cell))
                    {
                        foreach (var part in TextRules.SplitIndicators(paragraph))
                        {
                            if (TextRules.TryParseCode(part, out var code, out var tail))
                            {
                                startedWithCode = true;
                                if (code.Indicator is null)
                                {
                                    indicator = null;
                                    competence = AddCompetence(code, category, tail, sheet, r + 1);
                                }
                                else
                                {
                                    competence = null;
                                    indicator = new IndicatorDraft(code, tail, sheet, r + 1);
                                    indicators.Add(indicator);
                                }
                            }
                            else if (indicator is not null)
                                indicator.Text += " " + part; // перенос строки внутри формулировки
                            else if (competence is not null)
                                competence.Formulation += " " + part;
                        }
                    }

                    // Ячейка без кодов перед ячейкой компетенции — наименование категории (у УК).
                    if (!startedWithCode)
                        category ??= WordTables.CellText(cell) is { Length: > 0 } c ? c : null;
                }
            }
        }

        CompetenceDraft? AddCompetence(CompetenceCode code, string? category, string formulation, string sheet, int row)
        {
            if (byCode.ContainsKey(code.Competence))
            {
                log.Warning(F, sheet, row, $"Компетенция {code.Competence} повторяется — оставлена первая");
                return null;
            }
            var draft = new CompetenceDraft(code, category, formulation);
            byCode[code.Competence] = draft;
            competences.Add(draft);
            return draft;
        }

        // Индикатор относится к компетенции по префиксу кода: ПК-1.1.1 → ПК-1.
        foreach (var indicator in indicators)
        {
            if (!byCode.TryGetValue(indicator.Code.Competence, out var owner))
            {
                log.Error(F, indicator.Sheet, indicator.Row,
                    $"Индикатор {indicator.Code.Indicator}: нет компетенции {indicator.Code.Competence} — индикатор пропущен");
                continue;
            }
            if (owner.Indicators.Any(i => i.Code.Indicator == indicator.Code.Indicator))
            {
                log.Warning(F, indicator.Sheet, indicator.Row, $"Индикатор {indicator.Code.Indicator} повторяется — оставлен первый");
                continue;
            }
            owner.Indicators.Add(indicator);
        }

        if (competences.Count == 0)
            log.Error(F, null, null, "Не найдено ни одной компетенции");

        var result = competences.Select(c =>
        {
            var formulation = Tidy(c.Formulation);
            if (formulation.Length == 0)
                log.Error(F, null, null, $"Компетенция {c.Code.Competence}: нет формулировки");
            if (c.Indicators.Count == 0)
                log.Warning(F, null, null, $"Компетенция {c.Code.Competence}: нет индикаторов");

            var list = c.Indicators.Select(i =>
            {
                var text = Tidy(i.Text);
                var type = TextRules.IndicatorTypeOf(text);
                if (type is null)
                    log.Warning(F, i.Sheet, i.Row, $"Индикатор {i.Code.Indicator}: тип (знать / уметь / владеть) не распознан");
                return new CompetenceIndicator(i.Code.Indicator!, text, type);
            }).ToList();

            return new Competence(c.Code.Competence, c.Code.Category, c.CategoryName, formulation, list);
        }).ToList();

        return (direction.Success ? direction.Value : null, result);
    }

    // Точка с запятой в конце — остаток оформления перечня в документе, в формулировку не входит.
    private static string Tidy(string text) => TextRules.Clean(text).TrimEnd(';', ' ');
}
