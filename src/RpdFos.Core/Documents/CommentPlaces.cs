using System.Text.Json.Nodes;

namespace RpdFos.Core.Documents;

/// <summary>
/// Место замечания после правки списка (этап 8, A14). Замечание хранит путь с номером записи (/literature/main/2/citation),
/// а у записей списка устойчивого номера нет: схемы шаблонов не допускают лишних свойств, и старые документы не должны
/// ломаться. Поэтому при сохранении раздела записи прежнего и нового списка сопоставляются по содержимому, а путь
/// замечания переписывается на новый номер своей записи. Удалённая запись — замечание остаётся с прежним путём и
/// признаком «запись удалена»; другой записи оно не достаётся.
/// </summary>
public static class CommentPlaces
{
    /// <summary>Новый путь замечания и признак, что его запись удалена (путь тогда — прежний).</summary>
    public readonly record struct Place(string Path, bool RecordDeleted);

    /// <summary>
    /// Путь <paramref name="path"/> (JSON Pointer, первый сегмент — раздел) после перехода раздела из <paramref name="before"/>
    /// в <paramref name="after"/>. Номера записей — на каждом уровне вложенных списков.
    /// </summary>
    public static Place Remap(JsonNode? before, JsonNode? after, string path)
    {
        var segments = path.TrimStart('/').Split('/');
        var result = new List<string> { segments[0] };
        JsonNode? old = before, now = after;
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (old is JsonArray oldList && int.TryParse(segment, out var index) && index >= 0 && index < oldList.Count)
            {
                // Список исчез целиком — записей нет.
                if (now is not JsonArray newList) return new(path, true);
                if (Match(oldList, newList)[index] is not { } moved) return new(path, true);
                result.Add(moved.ToString());
                old = oldList[index];
                now = newList[moved];
            }
            else
            {
                result.Add(segment);
                var key = Unescape(segment);
                old = (old as JsonObject)?[key];
                now = (now as JsonObject)?[key];
                // Прежнего значения нет — глубже сопоставлять нечего, остаток пути не меняется.
                if (old is null) result.AddRange(segments[(i + 1)..]);
                if (old is null) break;
            }
        }
        return new("/" + string.Join('/', result), false);
    }

    /// <summary>
    /// Для каждой записи прежнего списка — её номер в новом (null — запись удалена). Порядок сопоставления: те же записи
    /// (по месту, затем по ближайшему номеру); записи, изменённые вместе со сдвигом, — по числу совпавших значений;
    /// остальные — по порядку, если неснятых в обоих списках поровну, иначе только на прежнем месте.
    /// </summary>
    internal static int?[] Match(JsonArray before, JsonArray after)
    {
        var map = new int?[before.Count];
        var taken = new bool[after.Count];

        for (var i = 0; i < before.Count && i < after.Count; i++)
            if (JsonNode.DeepEquals(before[i], after[i])) (map[i], taken[i]) = (i, true);
        for (var i = 0; i < before.Count; i++)
        {
            if (map[i] is not null) continue;
            var found = Enumerable.Range(0, after.Count).Where(j => !taken[j] && JsonNode.DeepEquals(before[i], after[j]))
                .OrderBy(j => Math.Abs(j - i)).Cast<int?>().FirstOrDefault();
            if (found is { } j2) (map[i], taken[j2]) = (j2, true);
        }

        // Запись изменена и сдвинута сразу: чем больше значений совпало, тем вероятнее это она.
        var pairs = (from i in Enumerable.Range(0, before.Count) where map[i] is null
                from j in Enumerable.Range(0, after.Count) where !taken[j]
                let score = Similarity(before[i], after[j])
                where score > 0
                orderby score descending, Math.Abs(i - j), i
                select (i, j)).ToList();
        foreach (var (i, j) in pairs)
            if (map[i] is null && !taken[j]) (map[i], taken[j]) = (j, true);

        var left = Enumerable.Range(0, before.Count).Where(i => map[i] is null).ToList();
        var fresh = Enumerable.Range(0, after.Count).Where(j => !taken[j]).ToList();
        if (left.Count == fresh.Count)
            for (var k = 0; k < left.Count; k++) (map[left[k]], taken[fresh[k]]) = (fresh[k], true);
        else
            foreach (var i in left.Where(i => i < after.Count && !taken[i])) (map[i], taken[i]) = (i, true);
        return map;
    }

    // Число совпавших значений: у объектов — по именам свойств, у списков — по номерам, у остальных — равенство.
    private static int Similarity(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (JsonObject x, JsonObject y) => x.Sum(p => y.TryGetPropertyValue(p.Key, out var other) ? Similarity(p.Value, other) : 0),
        (JsonArray x, JsonArray y) => Enumerable.Range(0, Math.Min(x.Count, y.Count)).Sum(i => Similarity(x[i], y[i])),
        (null, _) or (_, null) => 0,
        _ => JsonNode.DeepEquals(a, b) ? 1 : 0
    };

    private static string Unescape(string segment) => segment.Replace("~1", "/").Replace("~0", "~");
}
