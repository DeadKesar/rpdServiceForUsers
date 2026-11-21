#:package DocumentFormat.OpenXml@3.5.1
#:property PublishAot=false
// Вторая версия DOCX-шаблона оформления ФОС (этап 7, B2): из FOS_TemplateBookmarks.docx — FOS_TemplateBookmarks_v2.docx.
// Исходный файл — приложения 3–7 Положения о порядке формирования ФОС вместе с образцами. В v2 образцы и сноски-указания
// убраны (в настоящих ФОС их нет), три приложения банка заданий заменены одним блоком части, добавлены закладки
// содержимого: паспорт, текущий контроль, оценочные средства, банк заданий, критерии оценок, билеты.
// Блоки исходного файла берутся по номерам (дочерние элементы тела документа); каждый сверяется с ожидаемым началом
// текста, чтобы скрипт отверг другой файл, а не испортил его молча.
// Запуск: dotnet run tests/fixtures/make-fos-v2.cs -- tests/fixtures/FOS_TemplateBookmarks.docx tests/fixtures/FOS_TemplateBookmarks_v2.docx
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

File.Copy(args[0], args[1], overwrite: true);
using var document = WordprocessingDocument.Open(args[1], true);
var main = document.MainDocumentPart!;
var body = main.Document!.Body!;
var nextId = body.Descendants<BookmarkStart>().Select(mark => int.Parse(mark.Id!.Value!)).Max() + 1;
// Номера блоков — по исходному файлу; список снимается до правок, поэтому номера не сдвигаются.
var b = body.ChildElements.ToList();
const int Last = 287;

Expect(Last, null); // последний блок — свойства последнего раздела
Expect(70, "Паспорт"); Expect(71, "AutofillPassDestination"); Expect(72, ""); Expect(73, "№ п/п");
Expect(74, "* Наименования разделов"); Expect(75, ""); Expect(76, "Код и наименование компетенции");
Expect(78, "**Структурные компоненты"); Expect(86, "Пример оформления"); Expect(99, ""); Expect(101, "Приложение № 3");
Expect(102, "к Положению"); Expect(105, ""); Expect(112, "Кафедра"); Expect(116, "Вопросы для подготовки к");
Expect(118, "AutofillMonitoringU"); Expect(120, "по дисциплине"); Expect(122, "1…"); Expect(130, "Составитель");
Expect(131, "(ученая степень"); Expect(134, "Образец оформления вопросов"); Expect(138, ""); Expect(139, "Приложение № 4");
Expect(217, ""); Expect(218, "Приложение № 6"); Expect(221, ""); Expect(227, "20_"); Expect(230, "ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ");
Expect(232, "Для обучающихся"); Expect(234, "1."); Expect(235, "2."); Expect(236, "Зав. кафедрой"); Expect(237, "(подпись)");
Expect(240, "* Структура билета"); Expect(243, "Форма экзаменационного билета"); Expect(249, ""); Expect(250, "Приложение № 7");

var heading = (Paragraph)b[70];
var headingRun = (RunProperties)heading.Elements<Run>().First().RunProperties!.CloneNode(true);

// 1. Паспорт. Сноски-указания и звёздочки в заголовках таблиц убираются; пример паспорта из Положения (84–98) —
// тоже: он образец, а не форма. Разрыв раздела паспорта (99) остаётся.
b[74].Remove();
b[78].Remove();
for (var i = 79; i <= 98; i++) b[i].Remove();
foreach (var table in new[] { (Table)b[73], (Table)b[76] })
    foreach (var text in table.Descendants<Text>().ToList())
    {
        text.Text = text.Text.TrimEnd('*');
        if (text.Text.Length == 0) text.Parent!.Remove();
    }
// Таблица 1 паспорта (разделы дисциплины): закладка вокруг таблицы — как у таблицы 2; начало — в пустом абзаце перед
// таблицей, конец — в начале абзаца после неё (там же начинается закладка таблицы 2).
var sectionsId = NewId();
AtEnd((Paragraph)b[72], new BookmarkStart { Id = sectionsId, Name = "AutofillPassportSections1" });
AtStart((Paragraph)b[75], new BookmarkEnd { Id = sectionsId });

// 2. Текущий контроль и методические материалы: новые разделы после паспорта, в одном разделе Word.
var afterPassport = (Paragraph)b[99];
var sectionBreak = (Paragraph)b[99].CloneNode(true);
var newSections = new List<OpenXmlElement>
{
    Heading("Текущий контроль"), Placeholder("AutofillCurrentControl"),
    Heading("Методические материалы, определяющие процедуры оценивания"), Placeholder("AutofillMeans"),
    sectionBreak
};
Insert(afterPassport, newSections);

// 3. Банк заданий: один блок части вместо приложений 3–5. Заготовка — приложение 3; «Приложение № N к Положению…»
// и образцы убираются, заголовок и список — закладки, составитель — бланк в закладке.
for (var i = 100; i <= 104; i++) b[i].Remove();
b[117].Remove();
b[118].Remove();
var title = (Paragraph)b[116];
var titleProperties = (RunProperties)title.Elements<Run>().First().RunProperties!.CloneNode(true);
foreach (var run in title.Elements<Run>().ToList()) run.Remove();
Wrap(title, "AutofillBankTitle1", Text("AutofillBankTitle", titleProperties));
var tasks = (Paragraph)b[122];
foreach (var run in tasks.Elements<Run>().ToList()) run.Remove();
Wrap(tasks, "AutofillBankTasks1", Text("AutofillBankTasks", null));
b[123].Remove();
b[124].Remove();
for (var i = 126; i <= 129; i++) b[i].Remove();
var compilersId = NewId();
AtStart((Paragraph)b[130], new BookmarkStart { Id = compilersId, Name = "AutofillBankCompilers1" });
((Paragraph)b[131]).Append(new BookmarkEnd { Id = compilersId });
for (var i = 132; i <= 137; i++) b[i].Remove();
// Весь блок части — от эмблемы до разрыва раздела: генератор повторяет его по числу частей.
var groupId = NewId();
AtStart((Paragraph)b[105], new BookmarkStart { Id = groupId, Name = "AutofillBankGroup1" });
((Paragraph)b[138]).Append(new BookmarkEnd { Id = groupId });
// Приложения 4 и 5 — те же блоки с другими заголовками: заменены блоком части.
for (var i = 139; i <= 217; i++) b[i].Remove();

// 4. Критерии формирования оценок: раздел перед билетами; его разрыв стоит в начале блока билетов, чтобы без билетов
// раздел оставался последним и документ не кончался пустой страницей.
var examingId = ((BookmarkStart)b[218].Descendants<BookmarkStart>().First(s => s.Name == "AutofillExaming1")).Id!.Value!;
var ticketsBreak = (Paragraph)b[99].CloneNode(true);
AtStart(ticketsBreak, new BookmarkStart { Id = examingId, Name = "AutofillExaming1" });
Insert(b[138], [Heading("Критерии формирования оценок"), Placeholder("AutofillScale"), ticketsBreak]);

// 5. Билеты: приложение 6 — вся оболочка закладки AutofillExaming1; внутри блок билета AutofillTicket
// (шапка вуза, учебный год, номер, шапка с курсом и направлением, вопросы, подпись заведующего).
for (var i = 218; i <= 220; i++) b[i].Remove();
var firstOfTicket = (Paragraph)b[221];
firstOfTicket.ParagraphProperties ??= new ParagraphProperties();
firstOfTicket.ParagraphProperties.PageBreakBefore = new PageBreakBefore(); // билет — страница, как в форме Положения
var ticketId = NewId();
AtStart(firstOfTicket, new BookmarkStart { Id = ticketId, Name = "AutofillTicket1" });

// Учебный год в шапке: «20_/ 20_ учебный год» — бланк в закладке.
var year = (Paragraph)b[227];
var yearRuns = year.Elements<Run>().ToList();
var yearProperties = (RunProperties)yearRuns[0].RunProperties!.CloneNode(true);
var yearTail = (RunProperties)yearRuns[2].RunProperties!.CloneNode(true);
foreach (var run in yearRuns) run.Remove();
Wrap(year, "AutofillAcademicYear1", Text("AutofillAcademicYear", yearProperties));
year.Append(Text(" учебный год", yearTail));

// «ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ №_» — бланк номера в закладке.
var number = (Paragraph)b[230];
var numberProperties = (RunProperties)number.Elements<Run>().First().RunProperties!.CloneNode(true);
foreach (var run in number.Elements<Run>().ToList()) run.Remove();
number.Append(Text("ЭКЗАМЕНАЦИОННЫЙ БИЛЕТ № ", numberProperties));
Wrap(number, "AutofillTicketNumber1", Text("AutofillTicketNumber", numberProperties));

// Вопросы билета: «1.» и «2.» — одна закладка, нумерацию ставит генератор (вопросов может быть больше двух).
var questions = (Paragraph)b[234];
var questionProperties = (RunProperties)questions.Elements<Run>().First().RunProperties!.CloneNode(true);
foreach (var run in questions.Elements<Run>().ToList()) run.Remove();
Wrap(questions, "AutofillTicketQuestions1", Text("AutofillTicketQuestions", questionProperties));
b[235].Remove();

// «Зав. кафедрой ______ ______»: подпись остаётся бланком, расшифровка — фамилия заведующего (место подписи листа согласования).
var chair = (Paragraph)b[236];
var chairProperties = (RunProperties)chair.Elements<Run>().First().RunProperties!.CloneNode(true);
foreach (var run in chair.Elements<Run>().ToList()) run.Remove();
chair.Append(Text("Зав. кафедрой ____________________________ ", chairProperties));
Wrap(chair, "AutofillPositionKafName2", Text("AutofillPositionKafName", chairProperties));

// Конец блока билета и оболочки — в подписи: дальше образцы приложения 6 и приложение 7 не нужны.
((Paragraph)b[237]).Append(new BookmarkEnd { Id = ticketId }, new BookmarkEnd { Id = examingId });
for (var i = 238; i < Last; i++) b[i].Remove();

// 6. Выделение жёлтым — пометки шаблона Положения («Ф.И.О.», «Для обучающихся»): в печать оно не должно попасть.
foreach (var highlight in body.Descendants<Highlight>().ToList()) highlight.Remove();

// 7. Эмблемы убранных приложений: части изображений без ссылок в документе не нужны. На одну часть может вести
// несколько связей, поэтому проверяются все.
var used = body.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().Select(blip => blip.Embed?.Value)
    .Concat(body.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().Select(data => data.RelationshipId?.Value))
    .ToHashSet();
foreach (var image in main.ImageParts.ToList())
    if (!main.Parts.Where(part => part.OpenXmlPart == image).Any(part => used.Contains(part.RelationshipId)))
        main.DeletePart(image);

main.Document.Save();
Console.WriteLine($"Готово: {args[1]}");

// --- помощники ---

void Expect(int index, string? start)
{
    var element = b[index];
    if (start is null)
    {
        if (element is SectionProperties) return;
        throw new InvalidOperationException($"Блок {index}: ожидались свойства раздела.");
    }
    if (start.Length == 0 ? element.InnerText.Trim().Length > 0 && element is not Table : !element.InnerText.TrimStart().StartsWith(start, StringComparison.Ordinal))
        throw new InvalidOperationException($"Блок {index}: ожидалось «{start}», найдено «{element.InnerText.Trim()}». Это не исходный файл оформления ФОС.");
}

string NewId() => (nextId++).ToString();

// Заголовок нового раздела — как «Паспорт»: по центру, полужирный.
Paragraph Heading(string text) =>
    new((ParagraphProperties)heading.ParagraphProperties!.CloneNode(true), Text(text, headingRun));

// Место содержимого раздела: абзац с закладкой, в которую генератор подставит текст или таблицу.
Paragraph Placeholder(string name)
{
    var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Default" }, new Justification { Val = JustificationValues.Both }));
    Wrap(p, name + "1", Text(name, null));
    return p;
}

// Новые блоки — после данного, в порядке списка.
static void Insert(OpenXmlElement after, IEnumerable<OpenXmlElement> blocks)
{
    foreach (var block in blocks.Reverse())
        after.InsertAfterSelf(block);
}

void Wrap(Paragraph p, string name, Run run)
{
    var id = NewId();
    p.Append(new BookmarkStart { Id = id, Name = name }, run, new BookmarkEnd { Id = id });
}

static Run Text(string text, RunProperties? properties) =>
    new((RunProperties?)properties?.CloneNode(true) ?? new RunProperties(), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

// Метка в начале абзаца — после его свойств: свойства абзаца по схеме идут первыми.
static void AtStart(Paragraph p, OpenXmlElement mark)
{
    if (p.ParagraphProperties is { } properties) properties.InsertAfterSelf(mark);
    else p.PrependChild(mark);
}

static void AtEnd(Paragraph p, OpenXmlElement mark) => p.Append(mark);
