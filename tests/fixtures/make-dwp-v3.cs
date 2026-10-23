#:package DocumentFormat.OpenXml@3.5.1
#:property PublishAot=false
// Третья версия DOCX-шаблона оформления РПД (этап 5.1): из DWP_TemplateBookmarks_v2.docx — DWP_TemplateBookmarks_v3.docx.
// Постоянные пункты списков вуза (интернет-ресурсы 4.3, программное обеспечение 4.7, материально-техническое
// обеспечение 5) убираются из DOCX: они стали значениями по умолчанию шаблона документа. Остаются закладки списков —
// элементы списков шаблона, поэтому пункты по умолчанию и пункты автора печатаются одним списком с единой нумерацией.
// Убранные пункты выводятся в консоль — по ним сверены начальные умолчания (DocumentTypes/rpd.default-payload.json).
// Запуск: dotnet run tests/fixtures/make-dwp-v3.cs -- tests/fixtures/DWP_TemplateBookmarks_v2.docx tests/fixtures/DWP_TemplateBookmarks_v3.docx
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

File.Copy(args[0], args[1], overwrite: true);
using var document = WordprocessingDocument.Open(args[1], true);
var body = document.MainDocumentPart!.Document!.Body!;
List<Paragraph> Paragraphs() => body.Elements<Paragraph>().ToList();
Paragraph Bookmark(string name) => body.Descendants<BookmarkStart>().First(b => b.Name == name).Ancestors<Paragraph>().First();
// Заголовок раздела — последний абзац с таким началом: первый — строка оглавления.
Paragraph Heading(string start) => Paragraphs().Last(p => p.InnerText.StartsWith(start, StringComparison.Ordinal));

// 4.3 и 4.7: закладка — первый элемент списка, постоянные пункты идут за ней до следующего заголовка.
RemoveAfter(Bookmark("AutofillInternetResources1"), Heading("4.4"), "4.3");
RemoveAfter(Bookmark("AutofillSoftware1"), Heading("5."), "4.7");

// 5: постоянные пункты — свой список перед закладкой, а закладка вне списка: нумерация пунктов автора начиналась
// заново. Закладка получает оформление элемента списка вуза, пункты убираются.
var support = Bookmark("AutofillMaterialSupport1");
var items = Paragraphs().Where(p => p.ParagraphProperties?.NumberingProperties is not null &&
                                    p.ElementsAfter().Contains(support) && Paragraphs().IndexOf(p) > Paragraphs().IndexOf(Heading("5."))).ToList();
support.ParagraphProperties = (ParagraphProperties)items[0].ParagraphProperties!.CloneNode(true);
foreach (var p in items)
{
    Console.WriteLine($"5: {p.InnerText.Trim()}");
    p.Remove();
}

document.MainDocumentPart.Document.Save();
Console.WriteLine($"Готово: {args[1]}");

// Элементы того же списка после абзаца с закладкой — до заголовка следующего раздела.
void RemoveAfter(Paragraph token, Paragraph next, string section)
{
    var list = token.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value;
    foreach (var p in token.ElementsAfter().OfType<Paragraph>().TakeWhile(p => p != next).ToList())
        if (p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value == list && p.InnerText.Trim().Length > 0)
        {
            Console.WriteLine($"{section}: {p.InnerText.Trim()}");
            p.Remove();
        }
}
