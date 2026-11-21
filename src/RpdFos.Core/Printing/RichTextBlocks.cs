using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RpdFos.Core.Documents;

namespace RpdFos.Core.Printing;

/// <summary>
/// Форматированный текст документа (разметка после санитайзера: p, br, strong/b, em/i, ul, ol, li, table, img) — в блоки
/// печатной формы. Текст вне абзацев — свой абзац, br — новый абзац, элементы списков — абзацы списка
/// (вложенный список — тоже элементы, без уровней). Таблица — один блок: строки, ячейки с объединением и своими
/// абзацами; вложенной таблицы санитайзер не оставляет, а пришедшая в ячейку иным путём печатается её текстом.
/// </summary>
public static class RichTextBlocks
{
    private static readonly HtmlParser Parser = new();

    public static IReadOnlyList<PrintBlock> Parse(string html, Func<int> nextListId)
    {
        var body = Parser.ParseDocument("").Body!;
        body.InnerHtml = html;
        return new Reader(nextListId, tables: true).Read(body);
    }

    private sealed class Reader(Func<int> nextListId, bool tables)
    {
        private readonly List<PrintBlock> _result = [];
        private List<PrintRun> _runs = [];

        public IReadOnlyList<PrintBlock> Read(INode container)
        {
            Blocks(container);
            Flush(PrintListKind.None, 0);
            return _result;
        }

        private void Blocks(INode parent)
        {
            foreach (var node in parent.ChildNodes)
                switch (node)
                {
                    case IElement { LocalName: "p" } p:
                        Flush(PrintListKind.None, 0);
                        Inline(p, false, false);
                        Flush(PrintListKind.None, 0);
                        break;
                    case IElement { LocalName: "ul" or "ol" } list:
                        Flush(PrintListKind.None, 0);
                        List(list, list.LocalName == "ol" ? PrintListKind.Numbered : PrintListKind.Bullet, nextListId());
                        break;
                    default:
                        InlineNode(node, false, false);
                        break;
                }
        }

        private void List(IElement list, PrintListKind kind, int id)
        {
            foreach (var item in list.Children.Where(c => c.LocalName == "li"))
            {
                foreach (var child in item.ChildNodes)
                    if (child is IElement { LocalName: "ul" or "ol" } nested)
                    {
                        Flush(kind, id);
                        List(nested, nested.LocalName == "ol" ? PrintListKind.Numbered : PrintListKind.Bullet, nextListId());
                    }
                    else if (child is IElement { LocalName: "table" } table && tables)
                    {
                        // Текст пункта до таблицы остаётся пунктом списка.
                        Flush(kind, id);
                        AddTable(table);
                    }
                    else InlineNode(child, false, false);
                Flush(kind, id);
            }
        }

        private void Inline(INode parent, bool bold, bool italic)
        {
            foreach (var node in parent.ChildNodes) InlineNode(node, bold, italic);
        }

        private void InlineNode(INode node, bool bold, bool italic)
        {
            switch (node)
            {
                case IText text:
                    if (text.Data.Length > 0) _runs.Add(new PrintRun(text.Data, bold, italic));
                    break;
                case IElement { LocalName: "img" } image:
                    if (Image(image) is { } printed) _runs.Add(new PrintRun("", Image: printed));
                    break;
                case IElement { LocalName: "br" }:
                    Flush(PrintListKind.None, 0);
                    break;
                // Таблица внутри абзаца (так её может разобрать HTML без doctype) — всё равно отдельный блок.
                case IElement { LocalName: "table" } table when tables:
                    Flush(PrintListKind.None, 0);
                    AddTable(table);
                    break;
                case IElement e:
                    Inline(e, bold || e.LocalName is "strong" or "b", italic || e.LocalName is "em" or "i");
                    break;
            }
        }

        // Абзац из накопленных фрагментов; пробелы по краям абзаца не печатаются.
        private void Flush(PrintListKind kind, int listId)
        {
            if (_runs.Count > 0 && (_runs.Any(r => r.Image is not null) || !string.IsNullOrWhiteSpace(string.Concat(_runs.Select(r => r.Text)))))
            {
                _runs[0] = _runs[0] with { Text = _runs[0].Text.TrimStart() };
                _runs[^1] = _runs[^1] with { Text = _runs[^1].Text.TrimEnd() };
                _result.Add(new PrintParagraph(_runs.Where(r => r.Text.Length > 0 || r.Image is not null).ToList(), kind, listId));
            }
            _runs = [];
        }

        private void AddTable(IElement table)
        {
            // Строки — в порядке документа, из групп (thead, tbody, tfoot) и напрямую; пустые строки не печатаются.
            var sources = new List<IElement>();
            foreach (var child in table.Children)
                if (child.LocalName == "tr") sources.Add(child);
                else if (child.LocalName is "thead" or "tbody" or "tfoot") sources.AddRange(child.Children.Where(c => c.LocalName == "tr"));

            var rows = sources
                .Select(row => new PrintTableRow(row.Children.Where(c => c.LocalName is "td" or "th").Select(Cell).ToList()))
                .Where(row => row.Cells.Count > 0).ToList();
            if (rows.Count > 0) _result.Add(new PrintTable(rows));
        }

        // Содержимое ячейки — те же абзацы, что вне таблицы; вложенных таблиц в ячейке нет.
        private PrintTableCell Cell(IElement cell) =>
            new(new Reader(nextListId, tables: false).Read(cell).OfType<PrintParagraph>().ToList(),
                Span(cell, "colspan"), Span(cell, "rowspan"), cell.LocalName == "th");

        // Рисунок разметки: номер из собственного адреса и размер в точках, если он задан; чужой адрес санитайзер не оставил бы,
        // а пришедший иным путём не печатается.
        private static PrintImage? Image(IElement image) =>
            DocumentImages.IdsIn(image.GetAttribute("src")).Cast<Guid?>().FirstOrDefault() is { } id
                ? new PrintImage(id, Size(image, "width"), Size(image, "height"), image.GetAttribute("alt"))
                : null;

        private static int? Size(IElement image, string name) =>
            int.TryParse(image.GetAttribute(name), out var size) && size > 0 ? size : null;

        private static int Span(IElement cell, string name) =>
            int.TryParse(cell.GetAttribute(name), out var span) ? Math.Clamp(span, 1, RichTextSanitizer.MaxSpan) : 1;
    }
}
