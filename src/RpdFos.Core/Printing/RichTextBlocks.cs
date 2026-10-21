using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace RpdFos.Core.Printing;

/// <summary>
/// Форматированный текст документа (разметка после санитайзера: p, br, strong/b, em/i, ul, ol, li) — в абзацы
/// печатной формы. Текст вне абзацев — свой абзац, br — новый абзац, элементы списков — абзацы списка
/// (вложенный список — тоже элементы, без уровней).
/// </summary>
public static class RichTextBlocks
{
    private static readonly HtmlParser Parser = new();

    public static IReadOnlyList<PrintParagraph> Parse(string html, Func<int> nextListId)
    {
        var body = Parser.ParseDocument("").Body!;
        body.InnerHtml = html;
        var result = new List<PrintParagraph>();
        var runs = new List<PrintRun>();
        Blocks(body);
        Flush(PrintListKind.None, 0);
        return result;

        void Blocks(INode parent)
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

        void List(IElement list, PrintListKind kind, int id)
        {
            foreach (var item in list.Children.Where(c => c.LocalName == "li"))
            {
                foreach (var child in item.ChildNodes)
                    if (child is IElement { LocalName: "ul" or "ol" } nested)
                    {
                        Flush(kind, id);
                        List(nested, nested.LocalName == "ol" ? PrintListKind.Numbered : PrintListKind.Bullet, nextListId());
                    }
                    else InlineNode(child, false, false);
                Flush(kind, id);
            }
        }

        void Inline(INode parent, bool bold, bool italic)
        {
            foreach (var node in parent.ChildNodes) InlineNode(node, bold, italic);
        }

        void InlineNode(INode node, bool bold, bool italic)
        {
            switch (node)
            {
                case IText text:
                    if (text.Data.Length > 0) runs.Add(new PrintRun(text.Data, bold, italic));
                    break;
                case IElement { LocalName: "br" }:
                    Flush(PrintListKind.None, 0);
                    break;
                case IElement e:
                    Inline(e, bold || e.LocalName is "strong" or "b", italic || e.LocalName is "em" or "i");
                    break;
            }
        }

        // Абзац из накопленных фрагментов; пробелы по краям абзаца не печатаются.
        void Flush(PrintListKind kind, int listId)
        {
            if (runs.Count > 0 && !string.IsNullOrWhiteSpace(string.Concat(runs.Select(r => r.Text))))
            {
                runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
                runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
                result.Add(new PrintParagraph(runs.Where(r => r.Text.Length > 0).ToList(), kind, listId));
            }
            runs = [];
        }
    }
}
