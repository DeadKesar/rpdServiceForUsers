namespace RpdFos.Core.Printing;

public enum PrintListKind { None, Bullet, Numbered }

/// <summary>Фрагмент абзаца печатной формы: полужирный и курсив — из форматированного текста.</summary>
public sealed record PrintRun(string Text, bool Bold = false, bool Italic = false);

/// <summary>
/// Абзац печатной формы. Элемент списка знает вид списка и его номер: нумерация у каждого списка своя.
/// </summary>
public sealed record PrintParagraph(IReadOnlyList<PrintRun> Runs, PrintListKind List = PrintListKind.None, int ListId = 0)
{
    public string PlainText => string.Concat(Runs.Select(r => r.Text));

    public static PrintParagraph Plain(string text, bool bold = false) => new([new PrintRun(text, bold)]);
}

/// <summary>
/// Значение для закладки: один или несколько абзацев. Короткое значение подставляется вместо текста закладки
/// внутри абзаца; несколько абзацев или список заменяют абзац закладки целиком.
/// </summary>
public sealed record PrintValue(IReadOnlyList<PrintParagraph> Paragraphs)
{
    public static readonly PrintValue Empty = new([]);

    public static PrintValue Text(string? text) => string.IsNullOrWhiteSpace(text) ? Empty : new([PrintParagraph.Plain(text.Trim())]);

    public bool IsEmpty => Paragraphs.All(p => string.IsNullOrWhiteSpace(p.PlainText));

    public bool IsMultiline => Paragraphs.Count > 1 || Paragraphs.Any(p => p.List != PrintListKind.None);

    /// <summary>Значение одной строкой — для места внутри абзаца: абзацы через «; ».</summary>
    public string Inline => string.Join("; ", Paragraphs.Select(p => p.PlainText.Trim()).Where(t => t.Length > 0));
}

/// <summary>Отчёт о формировании: предупреждения (не найден подписант, нет формы слова) и незаполненные места.</summary>
public sealed record PrintReport(IReadOnlyList<string> Warnings, IReadOnlyList<string> Unfilled);
