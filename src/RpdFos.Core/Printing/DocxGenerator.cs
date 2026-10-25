using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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
            var roots = new List<OpenXmlElement?> { _main.Document?.Body };
            roots.AddRange(_main.HeaderParts.Select(p => (OpenXmlElement?)p.Header));
            roots.AddRange(_main.FooterParts.Select(p => (OpenXmlElement?)p.Footer));
            foreach (var root in roots.OfType<OpenXmlElement>())
                foreach (var bookmark in root.Descendants<BookmarkStart>()
                             .Where(b => b.Name?.Value?.StartsWith(DocxBookmarks.Prefix, StringComparison.Ordinal) == true).ToList())
                    Fill(root, bookmark);
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
                    return form == WordForms.Nom || value.IsMultiline ? value : PrintValue.Text(Declined(value.Inline, form, name));
                case BookmarkTargetKinds.Signature:
                    var seat = context.Signatories.Seat(goal);
                    return PrintValue.Text(Declined(part switch
                    {
                        SignatureParts.Name => seat?.Name,
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
                else if (element is BookmarkEnd end && end.Id?.Value == start.Id?.Value) break;
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
                else ReplaceRange(range, bookmark, value.Inline);
                return;
            }

            if (value.IsEmpty) unfilled.Add($"{name} — {missing}");
            var paragraph = text.Ancestors<Paragraph>().FirstOrDefault();
            var whole = paragraph is not null && paragraph.Descendants<Text>().Count() == 1 && text.Text.Trim() == baseName;
            if (whole && (value.IsMultiline || value.Paragraphs.Any(p => p.Runs.Any(r => r.Bold || r.Italic))))
                ReplaceParagraph(paragraph!, text.Ancestors<Run>().First(), value);
            else
            {
                text.Text = token.Replace(text.Text, _ => value.Inline, 1);
                text.Space = SpaceProcessingModeValues.Preserve;
            }
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
        /// с теми же свойствами. Оформление текста — как у заглушки, плюс полужирный и курсив значения.
        /// </summary>
        private void ReplaceParagraph(Paragraph paragraph, Run placeholder, PrintValue value)
        {
            var runProperties = placeholder.RunProperties?.CloneNode(true) as RunProperties;
            var paragraphProperties = paragraph.ParagraphProperties?.CloneNode(true) as ParagraphProperties;
            foreach (var child in paragraph.ChildElements.ToList())
                if (child is not ParagraphProperties and not BookmarkStart and not BookmarkEnd) child.Remove();

            var current = paragraph;
            for (var i = 0; i < value.Paragraphs.Count; i++)
            {
                var target = i == 0 ? paragraph : new Paragraph { ParagraphProperties = paragraphProperties?.CloneNode(true) as ParagraphProperties };
                Write(target, value.Paragraphs[i], runProperties);
                if (i > 0)
                {
                    current.InsertAfterSelf(target);
                    current = target;
                }
            }
        }

        private void Write(Paragraph paragraph, PrintParagraph content, RunProperties? runProperties)
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
                var properties = runProperties?.CloneNode(true) as RunProperties ?? new RunProperties();
                if (run.Bold) properties.Bold = new Bold();
                if (run.Italic) properties.Italic = new Italic();
                var element = new Run(properties, new Text(run.Text) { Space = SpaceProcessingModeValues.Preserve });
                if (end is not null) end.InsertBeforeSelf(element);
                else paragraph.AppendChild(element);
            }
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
