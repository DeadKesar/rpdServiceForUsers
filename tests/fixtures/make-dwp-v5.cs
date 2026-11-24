#:package DocumentFormat.OpenXml@3.5.1
#:property PublishAot=false
// Пятая версия DOCX-шаблона оформления РПД (этап 8, A13): из DWP_TemplateBookmarks_v4.docx — DWP_TemplateBookmarks_v5.docx.
// Оглавление строит генератор: поле TOC со статическими строками заменено закладкой AutofillToc — внутри по образцу строки на
// уровень (первый абзац — «1. Название», второй — «1.1. Название»). Чтобы строки по заголовкам были верны:
// — уровень структуры остался только у заголовков: в v4 он стоял и у пустых абзацев, и у фразы раздела 4.5;
// — «4.1Литература» — «4.1 Литература»: между номером и названием пробел, в 4.1–4.7 его не было;
// — закладки прежнего оглавления (_Toc…) убраны: часть стояла не у своих заголовков, свои генератор ставит сам;
// — «Содержание» и раздел 1 начинаются с новой страницы сами, а не двадцатью пустыми абзацами: строк оглавления теперь разное число.
// Так же начинается «ЛИСТ согласования»: шестнадцать пустых абзацев перед ним переходили на новую страницу, когда последний раздел
// кончался внизу, и перед листом согласования печаталась пустая страница.
// Стили «toc 1» и «toc 2» — с оформлением строк: после «Обновить поле» в Word вид не меняется.
// Запуск: dotnet run tests/fixtures/make-dwp-v5.cs -- tests/fixtures/DWP_TemplateBookmarks_v4.docx tests/fixtures/DWP_TemplateBookmarks_v5.docx
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

File.Copy(args[0], args[1], overwrite: true);
using var document = WordprocessingDocument.Open(args[1], true);
var main = document.MainDocumentPart!;
var body = main.Document!.Body!;
List<Paragraph> Paragraphs() => body.Elements<Paragraph>().ToList();
Exception Unexpected(string what) => new InvalidOperationException("В файле v4 ожидалось другое: " + what);

// --- закладки прежнего оглавления ---
var tocMarks = body.Descendants<BookmarkStart>().Where(b => b.Name!.Value!.StartsWith("_Toc", StringComparison.Ordinal)).ToList();
var tocIds = tocMarks.Select(b => b.Id!.Value).ToHashSet();
foreach (var end in body.Descendants<BookmarkEnd>().Where(e => tocIds.Contains(e.Id!.Value)).ToList()) end.Remove();
foreach (var start in tocMarks) start.Remove();

// --- оглавление: от строки с началом поля TOC до абзаца с его концом ---
var paragraphs = Paragraphs();
var first = paragraphs.FindIndex(p => p.Descendants<FieldCode>().Any(f => f.Text.TrimStart().StartsWith("TOC ", StringComparison.Ordinal)));
var closing = paragraphs.FindIndex(first + 1, p => p.InnerText.Length == 0 && p.Descendants<FieldChar>().Any(f => f.FieldCharType!.Value == FieldCharValues.End));
if (first < 0 || closing < 0) throw Unexpected("поле TOC в теле документа");
var lines = paragraphs.GetRange(first, closing - first);
var nextId = body.Descendants<BookmarkStart>().Select(b => int.Parse(b.Id!.Value!)).Max() + 1;

// Образец строки: абзац прежней строки уровня без содержимого, текст — имя закладки. Табуляция у номера уровня 1 — позиция
// слева в свойствах абзаца, номер страницы — позиция справа с точками: генератор смотрит на них.
Paragraph Sample(string style)
{
    var line = lines.First(p => p.ParagraphProperties!.ParagraphStyleId!.Val!.Value == style);
    var properties = (ParagraphProperties)line.ParagraphProperties!.CloneNode(true);
    properties.ParagraphMarkRunProperties = null;
    properties.SpacingBetweenLines = null;
    return new Paragraph(properties, new Run(new RunProperties(new NoProof()), new Text("AutofillToc")));
}
var level1 = Sample("11");
var level2 = Sample("23");
level1.ParagraphProperties!.InsertAfterSelf(new BookmarkStart { Id = nextId.ToString(), Name = "AutofillToc1" });
level2.AppendChild(new BookmarkEnd { Id = nextId.ToString() });
lines[0].InsertBeforeSelf(level1);
lines[0].InsertBeforeSelf(level2);
foreach (var line in lines) line.Remove();

// --- «Содержание», раздел 1 и лист согласования — с новой страницы, пустые абзацы (и разрыв страницы в них) до них не нужны ---
void StartPage(Paragraph paragraph)
{
    while (paragraph.PreviousSibling() is Paragraph { InnerText.Length: 0 } gap && !gap.Descendants().Any(e => e is BookmarkStart or BookmarkEnd))
        gap.Remove();
    paragraph.ParagraphProperties!.PageBreakBefore = new PageBreakBefore();
}
StartPage(Paragraphs().Single(p => p.InnerText == "Содержание"));
var heading1 = Paragraphs().Single(p => p.InnerText == "Цели и задачи освоения дисциплины");
StartPage(heading1);
// Отступ перед заголовком у верхнего края страницы раньше не печатался, а после явного разрыва печатается.
heading1.ParagraphProperties!.SpacingBetweenLines!.Before = "0";
StartPage(Paragraphs().Single(p => p.InnerText == "ЛИСТ"));

// --- заголовки ---
var spaced = 0;
foreach (var heading in Paragraphs().Where(p => p.ParagraphProperties?.OutlineLevel is not null))
    if (heading.Descendants<Text>().FirstOrDefault() is { } text && Regex.Match(text.Text, @"^(4\.\d)(?=\p{L})") is { Success: true } number)
    {
        text.Text = number.Value + " " + text.Text[number.Length..];
        spaced++;
    }
if (spaced != 7) throw Unexpected("заголовки 4.1–4.7 без пробела после номера");

// --- уровень структуры — только у заголовков: номер раздела стоит в тексте или его ставит список ---
string Content(Paragraph p) => string.Concat(p.Descendants().Select(e => e is Text t ? t.Text : e is TabChar ? " " : ""));
foreach (var paragraph in Paragraphs().Where(p => p.ParagraphProperties?.OutlineLevel is not null))
    if (!Regex.IsMatch(Content(paragraph), @"^\d+(\.\d+)*\.?\s+\S") && paragraph.ParagraphProperties!.NumberingProperties is null)
        paragraph.ParagraphProperties!.OutlineLevel = null;
var headings = Paragraphs().Where(p => p.ParagraphProperties?.OutlineLevel is not null).Select(Content).ToList();
if (headings.Count != 20) throw Unexpected("20 заголовков, а получилось " + headings.Count);

// --- стили строк оглавления ---
void Restyle(string id, params TabStop[] tabs)
{
    var style = main.StyleDefinitionsPart!.Styles!.Elements<Style>().Single(s => s.StyleId == id);
    var paragraph = new StyleParagraphProperties { Tabs = new Tabs(tabs.Select(t => t.CloneNode(true))) };
    paragraph.SpacingBetweenLines = new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto };
    style.StyleParagraphProperties = paragraph;
    style.StyleRunProperties = new StyleRunProperties(
        new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = "Times New Roman", ComplexScript = "Times New Roman" },
        new FontSize { Val = "24" }, new FontSizeComplexScript { Val = "24" }, new Languages { EastAsia = "ru-RU" });
}
TabStop[] Tabs(Paragraph p) => p.ParagraphProperties!.Tabs!.Elements<TabStop>().ToArray();
Restyle("11", Tabs(level1));
Restyle("23", Tabs(level2));

main.StyleDefinitionsPart!.Styles!.Save();
main.Document.Save();
Console.WriteLine($"Готово: {args[1]}; заголовки: {string.Join(" | ", headings)}");
