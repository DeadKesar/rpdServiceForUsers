using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Printing;

public sealed record DocxResult(byte[] Content, PrintReport Report);

/// <summary>
/// Заполнение DOCX-шаблона оформления по сопоставлению закладок шаблона документа (<c>print_mapping</c>).
/// Каждая закладка <c>Autofill&lt;Поле&gt;&lt;N&gt;</c> получает значение своей цели — одно значение во все одноимённые
/// закладки (fan-out), суффикс любой длины. Текст-заглушка закладки (её имя) заменяется значением с сохранением
/// оформления; несколько абзацев и списки заменяют абзац закладки целиком; цель вида <c>table</c> заполняет таблицу
/// внутри закладки построителем.
/// </summary>
public static partial class DocxGenerator
{
    public static DocxResult Generate(byte[] template, IReadOnlyDictionary<string, BookmarkTarget> mapping, PrintContext context)
    {
        using var stream = new MemoryStream();
        stream.Write(template);
        var warnings = new List<string>(context.Signatories.Warnings);
        var unfilled = new List<string>();
        using (var document = WordprocessingDocument.Open(stream, true))
            new Filler(document, mapping, context, warnings, unfilled).Run();
        return new DocxResult(stream.ToArray(), new PrintReport(warnings, unfilled));
    }

    // Подстановка {источник} в цели вида text.
    [GeneratedRegex(@"\{(?<source>[^{}]+)\}")]
    private static partial Regex Placeholder();

    // «0 зачетных единиц (328 академических часов)» — фраза трудоёмкости шаблона после заполнения закладок.
    [GeneratedRegex(@"\b0\s+зач[её]тн\w*\s+единиц\w*\s*\(\s*(?<hours>\d+)\s+академическ\w*\s+час\w*\s*\)")]
    private static partial Regex ZeroCredits();

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    private sealed class Filler(
        WordprocessingDocument document, IReadOnlyDictionary<string, BookmarkTarget> mapping, PrintContext context,
        List<string> warnings, List<string> unfilled)
    {
        private readonly MainDocumentPart _main = document.MainDocumentPart!;
        private readonly Dictionary<(PrintListKind, int), int> _numbering = [];
        private readonly Dictionary<PrintListKind, int> _abstractNumbering = [];

        public void Run()
        {
            if (_main.Document?.Body is { } main) ExpandBlocks(main);
            var roots = new List<OpenXmlElement?> { _main.Document?.Body };
            roots.AddRange(_main.HeaderParts.Select(p => (OpenXmlElement?)p.Header));
            roots.AddRange(_main.FooterParts.Select(p => (OpenXmlElement?)p.Footer));
            foreach (var root in roots.OfType<OpenXmlElement>())
                foreach (var bookmark in root.Descendants<BookmarkStart>()
                             .Where(b => b.Name?.Value?.StartsWith(DocxBookmarks.Prefix, StringComparison.Ordinal) == true).ToList())
                    Fill(root, bookmark);
            if (_main.Document?.Body is { } body) ZeroCreditLaboriousness(body);
        }

        /// <summary>
        /// Блоки (цели вида <c>block</c>, этап 7, B9): закладка охватывает целые абзацы и таблицы тела документа. «repeat» — блок
        /// печатается по экземпляру на элемент цели, каждый заполняется со своим элементом; нет элементов — блок убирается.
        /// «present» — блок остаётся, если у цели есть элементы, иначе убирается вместе с разрывом раздела внутри него.
        /// Блоки идут по порядку документа: внешний — раньше вложенного.
        /// </summary>
        private void ExpandBlocks(Body body)
        {
            while (body.Descendants<BookmarkStart>().FirstOrDefault(IsBlock) is { } start)
            {
                var target = mapping[DocxBookmarks.BaseName(start.Name!.Value!)];
                var first = TopLevel(start);
                var last = body.Descendants<BookmarkEnd>().FirstOrDefault(e => e.Id?.Value == start.Id?.Value) is { } closing ? TopLevel(closing) : first;
                var elements = new List<OpenXmlElement>();
                for (OpenXmlElement? element = first; element is not null; element = element.NextSibling())
                {
                    elements.Add(element);
                    if (element == last) break;
                }

                // Метки самого блока снимаются раньше копирования: иначе каждая копия стала бы блоком.
                foreach (var end in body.Descendants<BookmarkEnd>().Where(e => e.Id?.Value == start.Id?.Value).ToList()) end.Remove();
                start.Remove();

                var count = context.RepeatCount(target.Target);
                if (count == 0)
                {
                    foreach (var element in elements) element.Remove();
                    continue;
                }
                if (target.Part == BlockModes.Present) continue;

                OpenXmlElement anchor = last;
                for (var i = 0; i < count; i++)
                {
                    var copy = elements.Select(e => e.CloneNode(true)).ToList();
                    foreach (var element in copy)
                    {
                        anchor.InsertAfterSelf(element);
                        anchor = element;
                    }
                    using (context.Enter(target.Target, i))
                        foreach (var bookmark in copy.SelectMany(e => e.Descendants<BookmarkStart>().Concat(e is BookmarkStart b ? [b] : []))
                                     .Where(b => b.Name?.Value?.StartsWith(DocxBookmarks.Prefix, StringComparison.Ordinal) == true).ToList())
                            Fill(body, bookmark);
                    RenumberDrawings(copy);
                    Unbookmark(copy);
                }
                foreach (var element in elements) element.Remove();
            }
        }

        private bool IsBlock(BookmarkStart bookmark) =>
            bookmark.Name?.Value is { } name && name.StartsWith(DocxBookmarks.Prefix, StringComparison.Ordinal) &&
            mapping.TryGetValue(DocxBookmarks.BaseName(name), out var target) && target.Kind == BookmarkTargetKinds.Block;

        // Элемент тела, в котором стоит метка: сама метка, если она прямо в теле.
        private static OpenXmlElement TopLevel(OpenXmlElement element) =>
            element.Parent is Body ? element : element.Ancestors().First(a => a.Parent is Body);

        // У копии блока метки закладок не нужны: их имена и номера повторяли бы шаблонные.
        private static void Unbookmark(IEnumerable<OpenXmlElement> copy)
        {
            var all = copy.SelectMany(e => e.Descendants<BookmarkStart>().Concat(e is BookmarkStart b ? [b] : [])).ToList();
            var ids = all.Select(b => b.Id?.Value).ToHashSet();
            foreach (var end in copy.SelectMany(e => e.Descendants<BookmarkEnd>().Concat(e is BookmarkEnd x ? [x] : [])).Where(e => ids.Contains(e.Id?.Value)).ToList())
                end.Remove();
            foreach (var start in all) start.Remove();
        }

        // Эмблема в каждой копии блока — свой номер рисунка: одинаковые номера Word не любит.
        private void RenumberDrawings(IEnumerable<OpenXmlElement> copy)
        {
            foreach (var properties in copy.SelectMany(e => e.Descendants<DW.DocProperties>()))
                properties.Id = NextDrawingId();
        }

        /// <summary>
        /// Дисциплина без з. е. (элективы по физической культуре: 328 ч при 0 з. е.): «составляет 0 зачетных единиц
        /// (328 академических часов)» печатается, как в РПД вуза, — «составляет 328 академических часов в рамках
        /// элективных дисциплин (модулей)» (этап 5.4, B4). Фраза шаблона собрана из текста и двух закладок, поэтому
        /// правится абзац целиком; оформление — первого фрагмента абзаца.
        /// </summary>
        private void ZeroCreditLaboriousness(Body body)
        {
            var d = context.Facts.Discipline;
            if (d.TotalCredits != 0 || d.TotalHours <= 0) return;
            var tail = d.PartType == PartTypes.ElectiveItem ? " в рамках элективных дисциплин (модулей)" : "";
            foreach (var paragraph in body.Descendants<Paragraph>().ToList())
            {
                var texts = paragraph.Descendants<Text>().ToList();
                var whole = string.Concat(texts.Select(t => t.Text));
                var replaced = ZeroCredits().Replace(whole, m => $"{m.Groups["hours"].Value} академических часов{tail}");
                if (replaced == whole || texts.Count == 0) continue;
                texts[0].Text = replaced;
                texts[0].Space = SpaceProcessingModeValues.Preserve;
                foreach (var text in texts.Skip(1)) text.Text = "";
            }
        }

        private void Fill(OpenXmlElement root, BookmarkStart bookmark)
        {
            var name = bookmark.Name!.Value!;
            var baseName = DocxBookmarks.BaseName(name);
            if (!mapping.TryGetValue(baseName, out var target))
            {
                warnings.Add($"Закладка {name} не сопоставлена — оставлена как есть.");
                return;
            }
            var occurrence = target.Occurrences?.GetValueOrDefault(name);
            var kind = occurrence?.Kind ?? target.Kind;
            var goal = occurrence?.Target ?? target.Target;
            var part = occurrence?.Part ?? target.Part;
            var form = occurrence?.Form ?? WordForms.Nom;
            var stub = occurrence?.Stub ?? target.Stub;
            var range = Range(root, bookmark);

            if (kind == BookmarkTargetKinds.Block)
            {
                warnings.Add($"Закладка {name}: блок можно печатать только из тела документа — оставлена как есть.");
                return;
            }

            if (kind == BookmarkTargetKinds.Table)
            {
                if (range.OfType<Table>().FirstOrDefault() is { } table)
                    DocxTables.Build(goal, table, context, warnings, unfilled, name, stub);
                else warnings.Add($"Закладка {name}: внутри нет таблицы для построителя «{goal}».");
                return;
            }

            // Место не предусмотрено планом: формулировка заменяет всё содержимое закладки — своя у вхождения или общая
            // заглушка раздела.
            if (kind == BookmarkTargetKinds.Field && context.Stub(goal) is { } sectionStub)
            {
                ReplaceRange(range, bookmark, stub ?? sectionStub);
                return;
            }

            // Выбирать из плана не из чего (у дисциплины 1 семестра нет предшественников) — формулировка из сопоставления.
            if (kind == BookmarkTargetKinds.Field && context.NoCandidatesText(goal, target.NoCandidates) is { } noCandidates)
            {
                Place(range, bookmark, baseName, PrintValue.Text(noCandidates), name, Describe(kind, goal, part));
                return;
            }

            var value = Value(kind, goal, part, form, name);
            Place(range, bookmark, baseName, value, name, Describe(kind, goal, part));
        }

        private PrintValue Value(string kind, string goal, string? part, string form, string name)
        {
            switch (kind)
            {
                case BookmarkTargetKinds.Source:
                    return PrintValue.Text(Declined(context.Source(goal), form, name));
                case BookmarkTargetKinds.Field:
                    var value = context.Field(goal);
                    // «И. О. Фамилия» составителя — неразрывно.
                    if (goal.EndsWith("/name_for_document", StringComparison.Ordinal) && !value.IsEmpty) return PrintValue.Text(PrintNames.NoBreak(value.Inline));
                    return form == WordForms.Nom || value.IsMultiline ? value : PrintValue.Text(Declined(value.Inline, form, name));
                case BookmarkTargetKinds.Signature:
                    var seat = context.Signatories.Seat(goal);
                    return PrintValue.Text(Declined(part switch
                    {
                        SignatureParts.Name => seat?.Name is { } fullName ? PrintNames.NoBreak(fullName) : null,
                        SignatureParts.Position => seat?.Position,
                        SignatureParts.DocumentTitle => seat?.Department,
                        _ => null
                    }, form, name));
                case BookmarkTargetKinds.Text:
                    return PrintValue.Text(Placeholder().Replace(goal, m => context.Source(m.Groups["source"].Value) ?? ""));
                default:
                    warnings.Add($"Закладка {name}: неизвестный вид цели «{kind}».");
                    return PrintValue.Empty;
            }
        }

        private string? Declined(string? value, string form, string name)
        {
            if (string.IsNullOrEmpty(value) || form == WordForms.Nom) return value;
            if (form == WordForms.Lower) return char.ToLower(value[0], Ru) + value[1..];
            if (Declension.Form(value, form) is { } declined) return declined;
            warnings.Add($"Закладка {name}: нет формы «{form}» для «{value}» — напечатано как есть.");
            return value;
        }

        private static string Describe(string kind, string goal, string? part) => kind switch
        {
            BookmarkTargetKinds.Field => $"поле {goal} не заполнено",
            BookmarkTargetKinds.Source => $"в данных плана нет значения {goal}",
            BookmarkTargetKinds.Signature => $"подписант {goal} не найден ({part})",
            _ => $"пустое значение {goal}"
        };

        // Элементы между началом и концом закладки по порядку документа.
        private static List<OpenXmlElement> Range(OpenXmlElement root, BookmarkStart start)
        {
            var result = new List<OpenXmlElement>();
            var inside = false;
            foreach (var element in root.Descendants())
            {
                if (element == start) inside = true;
                else if (inside && element is BookmarkEnd end && end.Id?.Value == start.Id?.Value) break;
                else if (inside) result.Add(element);
            }
            return result;
        }

        private void Place(List<OpenXmlElement> range, BookmarkStart bookmark, string baseName, PrintValue value, string name, string missing)
        {
            // Заглушка — имя закладки без номера; «AutofillPositionKaf» не должна совпасть внутри «AutofillPositionKafName».
            var token = new Regex(Regex.Escape(baseName) + "(?![A-Za-z])");
            var text = range.OfType<Text>().FirstOrDefault(t => token.IsMatch(t.Text));
            if (text is null)
            {
                // Закладка вокруг бланка («________», «"___" ______ 20__ г.»): пустое значение бланк не стирает —
                // его заполнят вручную; значение заменяет бланк.
                if (value.IsEmpty) unfilled.Add($"{name} — {missing}, оставлен бланк");
                else if (value.Blocks.Count > 1 && range.OfType<Text>().FirstOrDefault() is { } first)
                {
                    // Бланк на нескольких абзацах («Составитель … (Ф.И.О.)»): остаётся первый абзац, значение — по абзацу на блок.
                    var run = first.Ancestors<Run>().First();
                    var owner = first.Ancestors<Paragraph>().First();
                    ReplaceRange(range, bookmark, "");
                    ReplaceParagraph(owner, run, value);
                }
                else ReplaceRange(range, bookmark, value.Inline);
                return;
            }

            if (value.IsEmpty) unfilled.Add($"{name} — {missing}");
            var paragraph = text.Ancestors<Paragraph>().FirstOrDefault();
            var whole = paragraph is not null && paragraph.Descendants<Text>().Count() == 1 && text.Text.Trim() == baseName;
            // Значение внутри абзаца с другим текстом печатается строкой: рисунку в ней места нет.
            if (!whole && value.Blocks.Any(b => b.HasImage))
                warnings.Add($"{name}: рисунок не напечатан — закладка стоит в абзаце с другим текстом, а рисунок печатается только вместо целого абзаца.");
            if (whole && value.ReplacesLead && !value.IsEmpty) DropLead(bookmark, paragraph!);
            if (whole && (value.IsMultiline || value.HasLink || value.Blocks.OfType<PrintParagraph>().Any(p => p.Runs.Any(r => r.Bold || r.Italic))))
                ReplaceParagraph(paragraph!, text.Ancestors<Run>().First(), value);
            else
            {
                text.Text = token.Replace(text.Text, _ => value.Inline, 1);
                Uncondense(text.Ancestors<Run>().FirstOrDefault()?.RunProperties);
                text.Space = SpaceProcessingModeValues.Preserve;
            }
        }

        // Заглушка шаблона бывает набрана со сжатием знаков (w:spacing < 0: в билете ФОС −1,3 пт), чтобы уместиться в строку;
        // значение другой длины с таким сжатием слипается, особенно в PDF — сжатие снимается.
        private static void Uncondense(RunProperties? properties)
        {
            if (properties?.Spacing?.Val?.Value < 0) properties.Spacing = null;
        }

        /// <summary>
        /// Всё содержимое закладки — одной строкой: текст — в первый текстовый элемент (с его оформлением), остальной
        /// текст закладки очищается, абзацы, начатые внутри закладки, удаляются (их метки закладок переносятся).
        /// Так заполняется бланк и печатается формулировка вместо раздела, не предусмотренного планом.
        /// </summary>
        private static void ReplaceRange(List<OpenXmlElement> range, BookmarkStart bookmark, string value)
        {
            var texts = range.OfType<Text>().ToList();
            if (texts.Count == 0)
            {
                if (bookmark.Parent is Paragraph)
                    bookmark.InsertAfterSelf(new Run(new Text(value) { Space = SpaceProcessingModeValues.Preserve }));
                return;
            }
            texts[0].Text = value;
            texts[0].Space = SpaceProcessingModeValues.Preserve;
            foreach (var text in texts.Skip(1)) text.Text = "";

            var first = texts[0].Ancestors<Paragraph>().FirstOrDefault();
            foreach (var paragraph in range.OfType<Paragraph>().Where(p => p != first).ToList())
            {
                foreach (var mark in paragraph.ChildElements.Where(c => c is BookmarkStart or BookmarkEnd).ToList())
                {
                    mark.Remove();
                    first?.AppendChild(mark);
                }
                paragraph.Remove();
            }
        }

        /// <summary>
        /// Абзац закладки — первый абзац значения (метки закладки и свойства абзаца остаются), остальные — следом
        /// с теми же свойствами. Таблица значения — настоящая таблица DOCX: стоящая до первого абзаца — перед абзацем
        /// закладки, остальные — по порядку. Оформление текста — как у заглушки, плюс полужирный и курсив значения.
        /// </summary>
        private void ReplaceParagraph(Paragraph paragraph, Run placeholder, PrintValue value)
        {
            var runProperties = placeholder.RunProperties?.CloneNode(true) as RunProperties;
            Uncondense(runProperties);
            var paragraphProperties = paragraph.ParagraphProperties?.CloneNode(true) as ParagraphProperties;
            foreach (var child in paragraph.ChildElements.ToList())
                if (child is not ParagraphProperties and not BookmarkStart and not BookmarkEnd) child.Remove();

            var width = TextWidth(paragraph);
            var tables = new List<Table>();
            OpenXmlElement current = paragraph;
            var used = false;
            foreach (var block in value.Blocks)
                switch (block)
                {
                    case PrintParagraph content when !used:
                        Write(paragraph, content, runProperties, width);
                        used = true;
                        break;
                    case PrintParagraph content:
                        var next = new Paragraph { ParagraphProperties = paragraphProperties?.CloneNode(true) as ParagraphProperties };
                        Write(next, content, runProperties, width);
                        current.InsertAfterSelf(next);
                        current = next;
                        break;
                    case PrintTable table:
                        foreach (var element in BuildTable(table, width, runProperties, paragraphProperties))
                        {
                            if (used)
                            {
                                current.InsertAfterSelf(element);
                                current = element;
                            }
                            else paragraph.InsertBeforeSelf(element);
                            if (element is Table inserted) tables.Add(inserted);
                        }
                        break;
                }

            // После таблицы — абзац: Word сливает таблицы, между которыми ничего нет, а ячейка (и конец документа)
            // не должна заканчиваться таблицей.
            foreach (var table in tables)
                if (table.NextSibling() is null or Table or SectionProperties)
                    table.InsertAfterSelf(new Paragraph());
        }

        // Таблица значения: рамки, ширина места закладки, столбцы поровну; объединения — gridSpan и vMerge.
        private IReadOnlyList<OpenXmlElement> BuildTable(PrintTable table, int width, RunProperties? runProperties, ParagraphProperties? paragraphProperties)
        {
            if (PrintTableGrid.Build(table) is not { } grid)
                // Строк нет или столбцов больше, чем бывает в Word: строки текста, слова не теряются.
                return table.Rows.Select(row =>
                {
                    var line = new Paragraph { ParagraphProperties = paragraphProperties?.CloneNode(true) as ParagraphProperties };
                    Write(line, PrintParagraph.Plain(string.Join(" | ", row.Cells.Select(c => c.PlainText))), runProperties, width);
                    return (OpenXmlElement)line;
                }).ToList();

            var columnWidth = Math.Max(width / grid.Columns, 1);
            var result = new Table(
                new TableProperties
                {
                    TableWidth = new TableWidth { Width = (columnWidth * grid.Columns).ToString(), Type = TableWidthUnitValues.Dxa },
                    TableBorders = Borders(),
                    TableLayout = new TableLayout { Type = TableLayoutValues.Fixed }
                },
                new TableGrid(Enumerable.Range(0, grid.Columns).Select(_ => new GridColumn { Width = columnWidth.ToString() })));
            foreach (var row in grid.Rows)
            {
                var tableRow = new TableRow();
                foreach (var placed in row) tableRow.AppendChild(BuildCell(placed, columnWidth, runProperties, paragraphProperties));
                result.AppendChild(tableRow);
            }
            return [result];
        }

        private TableCell BuildCell(PlacedCell placed, int columnWidth, RunProperties? runProperties, ParagraphProperties? paragraphProperties)
        {
            var properties = new TableCellProperties
            {
                TableCellWidth = new TableCellWidth { Width = (columnWidth * placed.Span).ToString(), Type = TableWidthUnitValues.Dxa }
            };
            if (placed.Span > 1) properties.GridSpan = new GridSpan { Val = placed.Span };
            if (placed.Merge == MergeKind.Restart) properties.VerticalMerge = new VerticalMerge { Val = MergedCellValues.Restart };
            else if (placed.Merge == MergeKind.Continue) properties.VerticalMerge = new VerticalMerge();
            var cell = new TableCell(properties);

            var header = placed.Cell?.Header == true;
            // Продолжение объединения пусто: текст ячейки напечатан выше.
            if (placed is { Merge: not MergeKind.Continue, Cell: { } source })
                foreach (var content in source.Paragraphs)
                {
                    var line = new Paragraph(CellParagraphProperties(paragraphProperties, header));
                    Write(line, header ? content with { Runs = content.Runs.Select(r => r with { Bold = true }).ToList() } : content, runProperties,
                        Math.Max(columnWidth * placed.Span - CellPadding, MinTableWidth / 4));
                    cell.AppendChild(line);
                }
            // Ячейка без абзаца — файл неверный.
            if (!cell.Elements<Paragraph>().Any()) cell.AppendChild(new Paragraph(CellParagraphProperties(paragraphProperties, header)));
            return cell;
        }

        // Абзац закладки мог быть пунктом списка, с красной строкой, по ширине или с разрывом раздела — в ячейке
        // ничего этого не нужно; шрифт и интервалы остаются, как у текста вокруг.
        private static ParagraphProperties CellParagraphProperties(ParagraphProperties? template, bool header)
        {
            var properties = template?.CloneNode(true) as ParagraphProperties ?? new ParagraphProperties();
            properties.NumberingProperties = null;
            properties.Indentation = null;
            properties.SectionProperties = null;
            properties.PageBreakBefore = null;
            properties.KeepNext = null;
            properties.Justification = new Justification { Val = header ? JustificationValues.Center : JustificationValues.Left };
            return properties;
        }

        private static TableBorders Borders() => new(
            new TopBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "auto" },
            new LeftBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "auto" },
            new BottomBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "auto" },
            new RightBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "auto" },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "auto" },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Space = 0, Color = "auto" });

        // Поля ячейки Word по умолчанию слева и справа, наименьшая ширина таблицы и ширина текста A4 с полями
        // 3 и 1,5 см — на случай шаблона без размеров страницы.
        private const int CellPadding = 216, MinTableWidth = 1000, DefaultTextWidth = 9355;

        // Таблица занимает ширину места закладки: ячейки, в которой стоит абзац, иначе — текста страницы раздела.
        private static int TextWidth(Paragraph paragraph)
        {
            var cellWidth = paragraph.Ancestors<TableCell>().FirstOrDefault()?.TableCellProperties?.TableCellWidth;
            if (cellWidth is { Type: { } type, Width: { } w } && type.Value == TableWidthUnitValues.Dxa && int.TryParse(w.Value, out var inner))
                return Math.Max(inner - CellPadding, MinTableWidth);

            var section = SectionOf(paragraph);
            var size = section?.GetFirstChild<PageSize>();
            var margin = section?.GetFirstChild<PageMargin>();
            if (size?.Width is { } page && margin is not null)
            {
                var text = (long)page.Value - (margin.Left?.Value ?? 0) - (margin.Right?.Value ?? 0);
                if (text >= MinTableWidth) return (int)text;
            }
            return DefaultTextWidth;
        }

        // Свойства раздела, в котором стоит абзац: ближайшие следом — в конце раздела (абзац с разрывом) или в конце тела.
        private static SectionProperties? SectionOf(Paragraph paragraph)
        {
            var block = paragraph.Ancestors().FirstOrDefault(a => a.Parent is Body) ?? paragraph;
            for (OpenXmlElement? element = block; element is not null; element = element.NextSibling())
                if (element is SectionProperties section) return section;
                else if (element is Paragraph { ParagraphProperties.SectionProperties: { } inner }) return inner;
            return null;
        }

        private void Write(Paragraph paragraph, PrintParagraph content, RunProperties? runProperties, int width)
        {
            // Заглушка — элемент списка шаблона: значения продолжают его список (4.3 с постоянными ресурсами вуза).
            if (content.List != PrintListKind.None && paragraph.ParagraphProperties?.NumberingProperties is null)
            {
                var properties = paragraph.ParagraphProperties ??= new ParagraphProperties();
                // Отступы задаёт уровень списка; отступ первой строки абзаца-заглушки списку мешает.
                properties.Indentation = null;
                properties.NumberingProperties = new NumberingProperties(
                    new NumberingLevelReference { Val = 0 }, new NumberingId { Val = NumberingId(content.List, content.ListId) });
            }
            var end = paragraph.Elements<BookmarkEnd>().FirstOrDefault();
            foreach (var run in content.Runs)
            {
                if (run.Image is not null)
                {
                    if (ImageRun(run.Image, width) is { } picture)
                        if (end is not null) end.InsertBeforeSelf(picture);
                        else paragraph.AppendChild(picture);
                    continue;
                }
                var properties = runProperties?.CloneNode(true) as RunProperties ?? new RunProperties();
                if (run.Bold) properties.Bold = new Bold();
                if (run.Italic) properties.Italic = new Italic();
                OpenXmlElement element = new Run(properties, new Text(run.Text) { Space = SpaceProcessingModeValues.Preserve });
                if (run.Url is not null && LinkId(run.Url) is { } link)
                {
                    // Гиперссылка — как её рисует Word, но прямым оформлением: стиля «Hyperlink» в шаблоне может не быть.
                    properties.Color = new Color { Val = "0563C1" };
                    properties.Underline = new Underline { Val = UnderlineValues.Single };
                    element = new Hyperlink(element) { Id = link, History = true };
                }
                if (end is not null) end.InsertBeforeSelf(element);
                else paragraph.AppendChild(element);
            }
        }

        // Внешняя ссылка пакета для адреса — одна на адрес; не http(s) или не разбирающийся адрес — null: печатается текстом.
        private readonly Dictionary<string, string> _links = [];

        private string? LinkId(string url)
        {
            if (_links.TryGetValue(url, out var existing)) return existing;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
            return _links[url] = _main.AddHyperlinkRelationship(uri, true).Id;
        }

        /// <summary>
        /// Значение заменяет вводную фразу закладки (<see cref="PrintValue.ReplacesLead"/>): абзац, в котором закладка началась, и
        /// абзацы до абзаца значения убираются. Метки закладок (в том числе чужие — ссылки оглавления «_Toc…») переезжают в абзац
        /// значения: оглавление и сама закладка остаются целыми.
        /// </summary>
        private static void DropLead(BookmarkStart start, Paragraph target)
        {
            if (start.Parent is not Paragraph lead || lead == target) return;
            var anchor = target.ParagraphProperties as OpenXmlElement;
            foreach (var mark in lead.ChildElements.Where(c => c is BookmarkStart or BookmarkEnd).ToList())
            {
                mark.Remove();
                if (anchor is null) target.InsertAt(mark, 0);
                else anchor.InsertAfterSelf(mark);
                anchor = mark;
            }
            for (var between = lead.NextSibling(); between is not null && between != target;)
            {
                var next = between.NextSibling();
                between.Remove();
                between = next;
            }
            lead.Remove();
        }

        // Рисунок форматированного текста (этап 7, B4а): размер из разметки (точки при 96 на дюйм), без размера — натуральный;
        // не шире места (текст страницы или ячейка) и не выше страницы, пропорции сохраняются. Один файл в нескольких местах —
        // одна часть пакета.
        private const long EmuPerPixel = 9525, EmuPerDxa = 635, MaxHeightEmu = 8_640_000;

        private readonly Dictionary<Guid, string> _imageParts = [];
        private uint _drawingId;

        // Номер для рисунка (docPr): следующий за наибольшим в документе, дальше по порядку.
        private uint NextDrawingId()
        {
            if (_drawingId == 0)
                _drawingId = (_main.Document?.Descendants<DW.DocProperties>().Select(p => p.Id?.Value ?? 0u).DefaultIfEmpty(0u).Max() ?? 0u) + 1;
            return _drawingId++;
        }

        private Run? ImageRun(PrintImage image, int widthDxa)
        {
            if (!context.Images.TryGetValue(image.FileId, out var info))
            {
                warnings.Add($"Рисунок {image.FileId} не найден — пропущен.");
                return null;
            }
            if (!_imageParts.TryGetValue(image.FileId, out var relationship))
            {
                var part = _main.AddImagePart(info.MimeType == ImageFiles.Png ? ImagePartType.Png : ImagePartType.Jpeg);
                using (var data = new MemoryStream(info.Content)) part.FeedData(data);
                _imageParts[image.FileId] = relationship = _main.GetIdOfPart(part);
            }

            double width = image.Width ?? (image.Height is { } h ? h * (double)info.Width / info.Height : info.Width);
            double height = image.Height ?? (image.Width is { } w ? w * (double)info.Height / info.Width : info.Height);
            double cx = width * EmuPerPixel, cy = height * EmuPerPixel;
            var scale = Math.Min(1.0, Math.Min(Math.Max(widthDxa, 1) * (double)EmuPerDxa / cx, MaxHeightEmu / cy));
            var extentX = Math.Max((long)Math.Round(cx * scale), 1);
            var extentY = Math.Max((long)Math.Round(cy * scale), 1);

            var id = NextDrawingId();
            return new Run(new Drawing(new DW.Inline(
                new DW.Extent { Cx = extentX, Cy = extentY },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = $"Рисунок {id}", Description = image.Alt },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"image{id}" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationship }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = extentX, Cy = extentY }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U }));
        }

        // Список: своё определение нумерации на каждый список, чтобы нумерованные списки начинались с 1.
        private int NumberingId(PrintListKind kind, int listId)
        {
            if (_numbering.TryGetValue((kind, listId), out var existing)) return existing;
            var part = _main.NumberingDefinitionsPart ?? _main.AddNewPart<NumberingDefinitionsPart>();
            var numbering = part.Numbering ??= new Numbering();

            if (!_abstractNumbering.TryGetValue(kind, out var abstractId))
            {
                abstractId = numbering.Elements<AbstractNum>().Select(a => a.AbstractNumberId?.Value ?? 0).DefaultIfEmpty(0).Max() + 1;
                var bullet = kind == PrintListKind.Bullet;
                var definition = new AbstractNum(
                    new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                    new Level(
                        new StartNumberingValue { Val = 1 },
                        new NumberingFormat { Val = bullet ? NumberFormatValues.Bullet : NumberFormatValues.Decimal },
                        new LevelText { Val = bullet ? "–" : "%1." },
                        new LevelJustification { Val = LevelJustificationValues.Left },
                        new PreviousParagraphProperties(new Indentation { Left = "1069", Hanging = "360" })) { LevelIndex = 0 })
                {
                    AbstractNumberId = abstractId
                };
                // Определения нумерации идут раньше экземпляров — так требует схема.
                if (numbering.Elements<NumberingInstance>().FirstOrDefault() is { } first) first.InsertBeforeSelf(definition);
                else numbering.AppendChild(definition);
                _abstractNumbering[kind] = abstractId;
            }

            var id = numbering.Elements<NumberingInstance>().Select(n => n.NumberID?.Value ?? 0).DefaultIfEmpty(0).Max() + 1;
            var instance = new NumberingInstance(new AbstractNumId { Val = abstractId }) { NumberID = id };
            if (kind == PrintListKind.Numbered)
                instance.AppendChild(new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 });
            numbering.AppendChild(instance);
            _numbering[(kind, listId)] = id;
            return id;
        }
    }
}
