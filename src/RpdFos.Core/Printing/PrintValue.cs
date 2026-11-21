namespace RpdFos.Core.Printing;

public enum PrintListKind { None, Bullet, Numbered }

/// <summary>
/// Рисунок форматированного текста (этап 7, B4а): файл и размер из разметки в точках; без размера — натуральный. Пропорции и
/// ширина места при печати — забота генератора.
/// </summary>
public sealed record PrintImage(Guid FileId, int? Width, int? Height, string? Alt);

/// <summary>
/// Фрагмент абзаца печатной формы: полужирный и курсив — из форматированного текста; рисунок — фрагмент без текста.
/// </summary>
public sealed record PrintRun(string Text, bool Bold = false, bool Italic = false, PrintImage? Image = null);

/// <summary>Блок значения для закладки: абзац или таблица (форматированный текст допускает и то и другое).</summary>
public abstract record PrintBlock
{
    public abstract string PlainText { get; }

    /// <summary>Есть ли в блоке рисунок: блок без слов, но с рисунком — не пустой.</summary>
    public abstract bool HasImage { get; }
}

/// <summary>
/// Абзац печатной формы. Элемент списка знает вид списка и его номер: нумерация у каждого списка своя.
/// </summary>
public sealed record PrintParagraph(IReadOnlyList<PrintRun> Runs, PrintListKind List = PrintListKind.None, int ListId = 0) : PrintBlock
{
    public override string PlainText => string.Concat(Runs.Select(r => r.Text));

    public override bool HasImage => Runs.Any(r => r.Image is not null);

    public static PrintParagraph Plain(string text, bool bold = false) => new([new PrintRun(text, bold)]);
}

/// <summary>
/// Ячейка таблицы: свои абзацы, объединение по столбцам и по строкам (как <c>colspan</c> и <c>rowspan</c> в HTML),
/// признак ячейки заголовка — её печатают полужирной и по центру, как показывает браузер.
/// </summary>
public sealed record PrintTableCell(IReadOnlyList<PrintParagraph> Paragraphs, int ColSpan = 1, int RowSpan = 1, bool Header = false)
{
    public string PlainText => string.Join(" ", Paragraphs.Select(p => p.PlainText.Trim()).Where(t => t.Length > 0));
}

public sealed record PrintTableRow(IReadOnlyList<PrintTableCell> Cells);

/// <summary>Таблица форматированного текста; сетку объединённых ячеек строит <see cref="PrintTableGrid"/>.</summary>
public sealed record PrintTable(IReadOnlyList<PrintTableRow> Rows) : PrintBlock
{
    /// <summary>Строки таблицы через «; », ячейки строки — через пробел: для места, где таблице не поместиться.</summary>
    public override bool HasImage => Rows.Any(r => r.Cells.Any(c => c.Paragraphs.Any(p => p.HasImage)));

    public override string PlainText => string.Join("; ", Rows
        .Select(r => string.Join(" ", r.Cells.Select(c => c.PlainText).Where(t => t.Length > 0)))
        .Where(t => t.Length > 0));
}

/// <summary>
/// Значение для закладки: один или несколько блоков. Короткое значение подставляется вместо текста закладки
/// внутри абзаца; несколько блоков, список или таблица заменяют абзац закладки целиком.
/// </summary>
public sealed record PrintValue(IReadOnlyList<PrintBlock> Blocks)
{
    public static readonly PrintValue Empty = new([]);

    public static PrintValue Text(string? text) => string.IsNullOrWhiteSpace(text) ? Empty : new([PrintParagraph.Plain(text.Trim())]);

    public bool IsEmpty => Blocks.All(b => string.IsNullOrWhiteSpace(b.PlainText) && !b.HasImage);

    public bool IsMultiline => Blocks.Count > 1 || Blocks.Any(b => b is PrintTable or PrintParagraph { List: not PrintListKind.None } || b.HasImage);

    /// <summary>Значение одной строкой — для места внутри абзаца: блоки через «; ».</summary>
    public string Inline => string.Join("; ", Blocks.Select(b => b.PlainText.Trim()).Where(t => t.Length > 0));
}

/// <summary>Отчёт о формировании: предупреждения (не найден подписант, нет формы слова) и незаполненные места.</summary>
public sealed record PrintReport(IReadOnlyList<string> Warnings, IReadOnlyList<string> Unfilled);
