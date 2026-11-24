using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace RpdFos.Core.Printing;

/// <summary>
/// Автоматические номера абзацев, какими их печатает Word: у определения списка — счётчик на уровень, абзац уровня начинает более
/// глубокие уровни заново. Нужны оглавлению: номера, который ставит список, в тексте заголовка нет (разделы 1–3 РПД). Считаются
/// только цифры — другими заголовки разделов не нумеруются. Абзацы передаются все и по порядку документа.
/// </summary>
internal sealed partial class DocxListNumbers(MainDocumentPart main)
{
    [GeneratedRegex("%(?<level>[1-9])")]
    private static partial Regex Placeholder();

    private readonly Numbering? _numbering = main.NumberingDefinitionsPart?.Numbering;
    private readonly Dictionary<AbstractNum, int[]> _counters = [];
    private readonly HashSet<int> _started = [];

    /// <summary>
    /// Номер абзаца: «1.», «2.1»; абзац не в списке — null, номер не цифрами — пустая строка. <paramref name="inherited"/> — свойства
    /// абзаца из стиля и его предков: список бывает и у стиля.
    /// </summary>
    public string? Next(Paragraph paragraph, IEnumerable<StyleParagraphProperties> inherited)
    {
        var list = paragraph.ParagraphProperties?.NumberingProperties
            ?? inherited.Select(p => p.NumberingProperties).FirstOrDefault(n => n is not null);
        // Номер списка 0 — Word так записывает снятую нумерацию.
        if (list?.NumberingId?.Val?.Value is not (> 0 and var id)) return null;
        var instance = _numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == id);
        if (instance?.AbstractNumId?.Val?.Value is not { } key ||
            _numbering!.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == key) is not { } definition) return null;
        var index = list.NumberingLevelReference?.Val?.Value ?? 0;
        if (LevelAt(index) is not { } level) return "";

        if (!_counters.TryGetValue(definition, out var counters)) _counters[definition] = counters = new int[9];
        // Список той же формы со своим началом («начать заново») считает с него, когда встретился впервые.
        var restart = instance.Elements<LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == index)?.StartOverrideNumberingValue?.Val?.Value;
        counters[index] = restart is { } from && _started.Add(id) ? from : counters[index] == 0 ? Start(index) : counters[index] + 1;
        Array.Clear(counters, index + 1, counters.Length - index - 1);

        return level.NumberingFormat?.Val?.Value != NumberFormatValues.Decimal ? ""
            : Placeholder().Replace(level.LevelText?.Val?.Value ?? "", m =>
            {
                var outer = int.Parse(m.Groups["level"].Value) - 1;
                return (counters[outer] == 0 ? Start(outer) : counters[outer]).ToString();
            });

        Level? LevelAt(int i) => definition.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == i);

        int Start(int i) => LevelAt(i)?.StartNumberingValue?.Val?.Value ?? 1;
    }
}
