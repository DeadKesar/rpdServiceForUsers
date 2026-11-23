#:package DocumentFormat.OpenXml@3.5.1
#:property PublishAot=false
// Четвёртая версия DOCX-шаблона оформления РПД (этап 7.1, шаг 4): из DWP_TemplateBookmarks_v3.docx — DWP_TemplateBookmarks_v4.docx.
// После раздела 5 «Материально-техническое обеспечение дисциплины» добавлен блок необязательного раздела 6: заголовок
// пишет автор (закладка AutofillAdditionalTitle), текст — форматированный (AutofillAdditionalText). Блок целиком
// (три пустых абзаца перед заголовком, как перед другими разделами, заголовок и текст) охвачен закладкой AutofillAdditionalSection:
// без текста раздела он убирается вместе с пустыми абзацами.
// Запуск: dotnet run tests/fixtures/make-dwp-v4.cs -- tests/fixtures/DWP_TemplateBookmarks_v3.docx tests/fixtures/DWP_TemplateBookmarks_v4.docx
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

File.Copy(args[0], args[1], overwrite: true);
using var document = WordprocessingDocument.Open(args[1], true);
var body = document.MainDocumentPart!.Document!.Body!;
var nextId = body.Descendants<BookmarkStart>().Select(b => int.Parse(b.Id!.Value!)).Max() + 1;
List<Paragraph> Paragraphs() => body.Elements<Paragraph>().ToList();

// Заголовок раздела 5 — последний абзац с таким началом (первый — строка оглавления); за ним — вводная фраза и список.
var heading5 = Paragraphs().Last(p => p.InnerText.StartsWith("5.", StringComparison.Ordinal) && p.InnerText.Contains("Материально-техническое"));
var intro = (Paragraph)heading5.NextSibling()!;
var support = Paragraphs().First(p => p.Descendants<BookmarkStart>().Any(b => b.Name == "AutofillMaterialSupport1"));
// Три пустых абзаца перед заголовком 5 — как перед любым разделом.
var gaps = new List<Paragraph>();
for (var p = heading5.PreviousSibling<Paragraph>(); gaps.Count < 3 && p is not null && p.InnerText.Length == 0; p = p.PreviousSibling<Paragraph>())
    gaps.Insert(0, p);
if (gaps.Count != 3) throw new InvalidOperationException("Перед заголовком раздела 5 ожидались три пустых абзаца");

var blockId = (nextId++).ToString();
var titleId = (nextId++).ToString();
var textId = (nextId++).ToString();

var anchor = (OpenXmlElement)support;
foreach (var gap in gaps)
{
    var copy = (Paragraph)gap.CloneNode(true);
    anchor.InsertAfterSelf(copy);
    anchor = copy;
}
// Начало закладки блока — в первом пустом абзаце, конец — в абзаце текста.
var first = (Paragraph)support.NextSibling()!;
var start = new BookmarkStart { Id = blockId, Name = "AutofillAdditionalSection1" };
if (first.ParagraphProperties is { } properties) properties.InsertAfterSelf(start);
else first.PrependChild(start);

// Заголовок: оформление заголовка 5; номер — текстом, как у остальных разделов.
var bold = new RunProperties(new Bold());
var heading = new Paragraph((ParagraphProperties)heading5.ParagraphProperties!.CloneNode(true));
heading.Append(
    new Run((RunProperties)bold.CloneNode(true), new Text("6.")),
    new Run((RunProperties)bold.CloneNode(true), new TabChar()),
    new BookmarkStart { Id = titleId, Name = "AutofillAdditionalTitle1" },
    new Run((RunProperties)bold.CloneNode(true), new Text("AutofillAdditionalTitle")),
    new BookmarkEnd { Id = titleId });
anchor.InsertAfterSelf(heading);

// Текст: оформление вводной фразы раздела 5 (абзац с красной строкой и выравниванием по ширине).
var textRun = (RunProperties)intro.Descendants<Run>().First().RunProperties!.CloneNode(true);
var textParagraph = new Paragraph((ParagraphProperties)intro.ParagraphProperties!.CloneNode(true));
textParagraph.Append(
    new BookmarkStart { Id = textId, Name = "AutofillAdditionalText1" },
    new Run(textRun, new Text("AutofillAdditionalText")),
    new BookmarkEnd { Id = textId },
    new BookmarkEnd { Id = blockId });
heading.InsertAfterSelf(textParagraph);

document.MainDocumentPart.Document.Save();
Console.WriteLine($"Готово: {args[1]}");
