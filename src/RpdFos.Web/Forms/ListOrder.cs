using System.Text.Json;
using System.Text.Json.Nodes;

namespace RpdFos.Web.Forms;

/// <summary>
/// Порядок записей в списках формы (этап 7.1). Общий приём: список, у которого в схеме стоит <c>x-reorderable</c>, получает у каждой
/// записи кнопки «выше» и «ниже» (<see cref="MoveButtons"/>); порядок в содержимом — это и порядок в печати. Вставки в начало нет:
/// новая запись добавляется в конец и поднимается кнопкой. Тем же приёмом пойдут группы и задания банка ФОС.
/// </summary>
public static class ListOrder
{
    /// <summary>Схема списка разрешает перестановку записей. Прежние шаблоны пометки не имеют — их формы остаются как были.</summary>
    public static bool IsReorderable(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("x-reorderable", out var flag) && flag.ValueKind == JsonValueKind.True;

    /// <summary>У первой записи нет «выше».</summary>
    public static bool CanMoveUp(int index) => index > 0;

    /// <summary>У последней записи нет «ниже».</summary>
    public static bool CanMoveDown(int index, int count) => index < count - 1;

    /// <summary>
    /// Сдвигает запись на одно место: <paramref name="delta"/> −1 — выше, к началу списка, +1 — ниже. Записи сохраняют узлы
    /// содержимого — меняется только их место. false — двигать некуда, список не тронут.
    /// </summary>
    public static bool Move(JsonArray items, int index, int delta)
    {
        var target = index + delta;
        if (delta is not (-1 or 1) || index < 0 || index >= items.Count || target < 0 || target >= items.Count) return false;
        var node = items[index];
        // Узел нельзя вставить, пока он принадлежит списку: сначала снять.
        items.RemoveAt(index);
        items.Insert(target, node);
        return true;
    }
}
