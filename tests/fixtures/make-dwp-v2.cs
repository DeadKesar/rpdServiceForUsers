#:package DocumentFormat.OpenXml@3.5.1
#:property PublishAot=false
// Новая версия DOCX-шаблона оформления РПД (этап 5): из DWP_TemplateBookmarks.docx — DWP_TemplateBookmarks_v2.docx.
// Инструкции шаблона (выделены жёлтым или красным) заменяются закладками; постоянные тексты вуза остаются.
// Запуск: dotnet run tests/fixtures/make-dwp-v2.cs -- tests/fixtures/DWP_TemplateBookmarks.docx tests/fixtures/DWP_TemplateBookmarks_v2.docx
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

File.Copy(args[0], args[1], overwrite: true);
using var document = WordprocessingDocument.Open(args[1], true);
var body = document.MainDocumentPart!.Document!.Body!;
var nextId = body.Descendants<BookmarkStart>().Select(b => int.Parse(b.Id!.Value!)).Max() + 1;
List<Paragraph> Paragraphs() => body.Elements<Paragraph>().ToList();
Paragraph Find(string start) => Paragraphs().First(p => p.InnerText.StartsWith(start, StringComparison.Ordinal));
// Заголовок раздела — последний абзац с таким началом: первый — строка оглавления.
Paragraph Heading(string start) => Paragraphs().Last(p => p.InnerText.StartsWith(start, StringComparison.Ordinal));
Paragraph After(Paragraph p) => p.ElementsAfter().OfType<Paragraph>().First();
List<Paragraph> Between(string from, string to)
{
    var all = Paragraphs();
    var a = all.IndexOf(Heading(from));
    var b = all.IndexOf(Heading(to));
    return all.GetRange(a + 1, b - a - 1);
}

// Оформление обычного текста раздела 4 — абзац «Методические указания к лабораторным занятиям…».
var bodyText = (ParagraphProperties)Find("Методические указания к лабораторным занятиям").ParagraphProperties!.CloneNode(true);
var headLine = (ParagraphProperties)Find("AutofillPositionKafForDoc").ParagraphProperties!.CloneNode(true);

// 1. Цели и задачи — пустые абзацы после вводных фраз.
Token(After(Find("Основной целью освоения дисциплины")), "AutofillGoals");
Token(After(Find("Основными задачами изучения дисциплины")), "AutofillObjectives");

// 2. Раздел 2: «Выбрать нужные из списка:» — инструкция перед списками дисциплин, удаляется.
foreach (var p in Paragraphs().Where(p => p.InnerText == "Выбрать нужные из списка:")) p.Remove();
// 2.1 — предварительные результаты обучения, 2.2 — результаты обучения по дисциплине: «Необходимо заполнить.».
var fill = Paragraphs().Where(p => p.InnerText == "Необходимо заполнить.").ToList();
string[] outcomes = ["AutofillPriorKnow", "AutofillPriorCan", "AutofillPriorOwn", "AutofillOutcomesKnow", "AutofillOutcomesCan", "AutofillOutcomesOwn"];
for (var i = 0; i < outcomes.Length; i++) Token(fill[i], outcomes[i]);

// 3.2 — таблица «Содержание разделов»: закладка вокруг таблицы, как у других таблиц шаблона.
var contents = body.Elements<Table>().First(t => t.InnerText.StartsWith("№ раздела", StringComparison.Ordinal));
var contentsId = (nextId++).ToString();
((Paragraph)contents.PreviousSibling()!).AppendChild(new BookmarkStart { Id = contentsId, Name = "AutofillDisciplineContentsTable1" });
AtStart(contents.ElementsAfter().OfType<Paragraph>().First(), new BookmarkEnd { Id = contentsId });

// 3.5 — заглушка AutofillKurs выделена жёлтым: выделение попало бы в печать.
foreach (var run in Find("По дисциплине").Descendants<Run>()) Clean(run.RunProperties);

// 4.1 — литература: основная и дополнительная списками.
var literature = Find("Создать нумерованный список литературы");
Plain(literature, "Основная литература:");
var main = New(literature, "AutofillLiteratureMain");
var label = New(main, null);
Plain(label, "Дополнительная литература:");
New(label, "AutofillLiteratureAdditional");

// 4.2 — периодические издания.
var periodicals = Find("Создать список периодических изданий");
periodicals.ParagraphProperties = (ParagraphProperties)bodyText.CloneNode(true);
Token(periodicals, "AutofillPeriodicals");

// 4.3 — «(5-9 обязательные)» в заголовке — инструкция; «Вставить» — место ресурсов дисциплины в списке перед
// постоянными ресурсами вуза: первый — закладка (элемент того же списка), остальные удаляются.
foreach (var run in Heading("4.3").Elements<Run>().Skip(1).ToList()) run.Remove(); // « (5-9 обязательные)» — красным
var resources = Between("4.3", "4.4").Where(p => p.InnerText == "Вставить").ToList();
Token(resources[0], "AutofillInternetResources");
foreach (var p in resources.Skip(1)) p.Remove();

// 4.4–4.6 — ссылки на методические указания: закладка от фразы до ссылки — если занятий нет в плане, формулировка
// заменяет и фразу.
Span(Find("Методические указания к лабораторным занятиям"), "AutofillGuidelinesLab");
Span(Find("Методические указания к практическим занятиям"), "AutofillGuidelinesPractice");
Span(Find("Методические указания к курсовому проектированию"), "AutofillGuidelinesIndependent");

// 4.7 — программное обеспечение: инструкции удаляются, «Вставить» — место списка дисциплины перед постоянным ПО.
Find("Создать нумерованный список программного обеспечения").Remove();
Find("3-10 - обязательные").Remove();
var software = Between("4.7", "5.").Where(p => p.InnerText == "Вставить").ToList();
Token(software[0], "AutofillSoftware");
foreach (var p in software.Skip(1)) p.Remove();

// 5 — материально-техническое обеспечение: дополнение к постоянному тексту вуза.
Token(Find("Дополнит список если необходимо."), "AutofillMaterialSupport");

// Протокол заседания кафедры: закладки вокруг бланков — пустой протокол оставляет подчёркивания для ручного заполнения.
var protocol = Find("протокол №");
var protocolRun = (RunProperties?)protocol.Descendants<RunProperties>().First().CloneNode(true);
foreach (var run in protocol.Elements<Run>().ToList()) run.Remove();
protocol.Append(Text("протокол № ", protocolRun));
Wrap(protocol, "AutofillProtocolNumber1", Text("________", protocolRun));
protocol.Append(Text(" от ", protocolRun));
Wrap(protocol, "AutofillProtocolDate1", Text("\"___\" __________ 20__г.", protocolRun));

// Исполнители: две строки по образцу строки заведующего («кафедра → подпись → расшифровка»), бланки должности
// и подписи — в закладках; составитель № 2 — те же закладки с номером 2.
var signer = Find("Исполнители:");
var first = After(signer);
var second = After(After(first));
Developer(first, 1);
Developer(second, 2);

document.MainDocumentPart.Document.Save();
Console.WriteLine($"Готово: {args[1]}");

// Абзац — закладка с текстом-заглушкой (её именем); оформление текста — как у первого фрагмента, без выделения.
void Token(Paragraph p, string name)
{
    var properties = p.Descendants<RunProperties>().FirstOrDefault()?.CloneNode(true) as RunProperties
                     ?? MarkProperties(p);
    Clean(properties);
    foreach (var run in p.Elements<Run>().ToList()) run.Remove();
    Wrap(p, name + "1", Text(name, properties));
}

// Новый абзац обычного текста после данного; с именем — закладка.
Paragraph New(Paragraph after, string? name)
{
    var p = new Paragraph { ParagraphProperties = (ParagraphProperties)bodyText.CloneNode(true) };
    after.InsertAfterSelf(p);
    if (name is not null) Wrap(p, name + "1", Text(name, MarkProperties(p)));
    return p;
}

void Plain(Paragraph p, string text)
{
    var properties = p.Descendants<RunProperties>().FirstOrDefault()?.CloneNode(true) as RunProperties;
    Clean(properties);
    foreach (var run in p.Elements<Run>().ToList()) run.Remove();
    p.ParagraphProperties = (ParagraphProperties)bodyText.CloneNode(true);
    p.AppendChild(Text(text, properties));
}

// Закладка от начала фразы до ссылки в следующем абзаце («Ссылка» — заглушка ссылки).
void Span(Paragraph sentence, string name)
{
    var link = After(sentence);
    var properties = (RunProperties?)sentence.Descendants<RunProperties>().FirstOrDefault()?.CloneNode(true);
    Clean(properties);
    foreach (var run in link.Elements<Run>().ToList()) run.Remove();
    link.ParagraphProperties = (ParagraphProperties)bodyText.CloneNode(true);
    var id = (nextId++).ToString();
    sentence.ParagraphProperties!.InsertAfterSelf(new BookmarkStart { Id = id, Name = name + "1" });
    link.Append(Text(name, properties), new BookmarkEnd { Id = id });
}

void Developer(Paragraph line, int n)
{
    var properties = (RunProperties)line.Descendants<RunProperties>().First().CloneNode(true);
    foreach (var child in line.ChildElements.Where(c => c is not ParagraphProperties).ToList()) child.Remove();
    line.ParagraphProperties = (ParagraphProperties)headLine.CloneNode(true);
    Wrap(line, $"AutofillDeveloperPosition{n}", Text(new string(' ', 40), properties));
    line.Append(new Run((RunProperties)properties.CloneNode(true), new TabChar()));
    Wrap(line, $"AutofillDeveloperName{n}", Text(new string(' ', 30), properties));
    line.Append(new Run((RunProperties)properties.CloneNode(true), new TabChar()));
}

void Wrap(Paragraph p, string name, Run run)
{
    var id = (nextId++).ToString();
    p.Append(new BookmarkStart { Id = id, Name = name }, run, new BookmarkEnd { Id = id });
}

static Run Text(string text, RunProperties? properties) =>
    new((RunProperties?)properties?.CloneNode(true) ?? new RunProperties(), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

static RunProperties MarkProperties(Paragraph p) =>
    new(p.ParagraphProperties?.ParagraphMarkRunProperties?.ChildElements.Select(c => c.CloneNode(true)) ?? []);

static void Clean(RunProperties? properties)
{
    if (properties is null) return;
    properties.Highlight = null;
    if (properties.Color?.Val?.Value == "FF0000") properties.Color = null;
}

// Метка в начале абзаца — после его свойств: свойства абзаца по схеме идут первыми.
static void AtStart(Paragraph p, OpenXmlElement mark)
{
    if (p.ParagraphProperties is { } properties) properties.InsertAfterSelf(mark);
    else p.PrependChild(mark);
}
