namespace RpdFos.Corpus;

/// <summary>Блок настоящего ФОС в порядке документа: абзац или таблица. У PDF блоков-таблиц нет: читаются только строки.</summary>
public abstract record FosBlock(int Index);

/// <param name="Heading">Похож на заголовок: стиль, «Приложение N», прописные, полужирная короткая строка.</param>
/// <param name="NumId">Автонумерация Word (список); null — нет или отключена.</param>
/// <param name="Formulas">Формулы в абзаце: OMML, объекты MathType, поля EQ.</param>
/// <param name="Drawings">Рисунки заданий: крупные DrawingML, VML, встроенные объекты, не похожие на формулы.</param>
/// <param name="SmallDrawings">Мелкие рисунки (эмблема вуза в шапке и подобное): в содержании не учитываются.</param>
public sealed record FosParagraph(
    int Index, string Text, bool Heading, bool Bold, bool Centered, int? NumId, int NumLevel, string NumFormat,
    int Formulas, int Drawings, int SmallDrawings, bool SectionEnd, bool PageBreak) : FosBlock(Index);

/// <param name="Merged">Ячеек, объединённых по горизонтали или вертикали: для таблиц в форматированном тексте (шаг B4).</param>
public sealed record FosTable(
    int Index, IReadOnlyList<IReadOnlyList<string>> Rows, int Formulas, int Drawings, int SmallDrawings, int Nested, int Merged) : FosBlock(Index)
{
    public int Cols => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

/// <summary>Закладка файла оформления: имя и блоки, между которыми она стоит (включительно).</summary>
public sealed record FosBookmark(string Name, int StartBlock, int EndBlock);

/// <param name="Id">Короткий номер в отчётах (F01…): имя файла в журнале и отчётах для репозитория не нужно.</param>
/// <param name="Source">Откуда читали: исходный файл или преобразованная копия (.doc → .docx).</param>
public sealed record FosDoc(
    string Id, string FileName, string Format, string Source, IReadOnlyList<FosBlock> Blocks,
    IReadOnlyList<FosBookmark> Bookmarks, int? Pages, int Images, int SmallImages, int TextChars, string? Error)
{
    public IEnumerable<FosParagraph> Paragraphs => Blocks.OfType<FosParagraph>();
    public IEnumerable<FosTable> Tables => Blocks.OfType<FosTable>();
}
