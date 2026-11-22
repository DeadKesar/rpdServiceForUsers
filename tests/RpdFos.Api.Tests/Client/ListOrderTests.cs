using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Web.Forms;
using Xunit;

namespace RpdFos.Api.Tests.Client;

/// <summary>
/// Порядок записей в списках формы (этап 7.1): кнопки «выше» и «ниже» у каждой записи, у первой нет «выше», у последней — «ниже».
/// Кнопки меняют порядок узлов содержимого — тот же порядок и печатается. Само отображение (кнопки, перерисовка строк) —
/// в браузере: компонентных тестов в проекте нет.
/// </summary>
public sealed class ListOrderTests
{
    private static JsonArray Items(params string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static string Order(JsonArray items) => string.Concat(items.Select(i => (string)i!));

    [Fact]
    public void The_last_of_three_records_is_raised_to_the_top_by_two_presses_of_up()
    {
        var items = Items("А", "Б", "В");

        Assert.True(ListOrder.Move(items, 2, -1));
        Assert.True(ListOrder.Move(items, 1, -1));

        Assert.Equal("ВАБ", Order(items));
    }

    [Fact]
    public void Down_moves_a_record_one_place_toward_the_end()
    {
        var items = Items("А", "Б", "В");

        Assert.True(ListOrder.Move(items, 0, 1));

        Assert.Equal("БАВ", Order(items));
    }

    [Theory]
    [InlineData(0, -1)] // у первой нет «выше»
    [InlineData(2, 1)]  // у последней нет «ниже»
    [InlineData(3, -1)] // записи с таким номером нет
    [InlineData(-1, 1)]
    [InlineData(1, 0)]  // сдвиг ровно на одно место
    [InlineData(1, 2)]
    public void A_move_with_nowhere_to_go_leaves_the_list_as_it_is(int index, int delta)
    {
        var items = Items("А", "Б", "В");

        Assert.False(ListOrder.Move(items, index, delta));

        Assert.Equal("АБВ", Order(items));
    }

    [Fact]
    public void Rows_keep_their_content_when_moved()
    {
        // Строки таблицы — те же объекты содержимого: правки полей, сделанные до перестановки, не теряются.
        var first = new JsonObject { ["citation"] = "первая" };
        var second = new JsonObject { ["citation"] = "вторая" };
        var items = new JsonArray(first, second);

        ListOrder.Move(items, 1, -1);

        Assert.Same(second, items[0]);
        Assert.Same(first, items[1]);
        Assert.Equal("вторая", (string?)items[0]!["citation"]);
    }

    [Theory]
    [InlineData(0, 3, false, true)]
    [InlineData(1, 3, true, true)]
    [InlineData(2, 3, true, false)]
    [InlineData(0, 1, false, false)] // единственная запись двигаться не может
    public void End_records_lack_one_of_the_buttons(int index, int count, bool up, bool down) =>
        Assert.Equal((up, down), (ListOrder.CanMoveUp(index), ListOrder.CanMoveDown(index, count)));

    [Theory]
    [InlineData("""{ "type": "array", "x-reorderable": true }""", true)]
    [InlineData("""{ "type": "array", "x-reorderable": false }""", false)]
    [InlineData("""{ "type": "array", "x-reorderable": "true" }""", false)]
    [InlineData("""{ "type": "array" }""", false)] // схема прежнего шаблона: форма остаётся как была
    public void Only_lists_marked_by_the_schema_are_reorderable(string schema, bool expected) =>
        Assert.Equal(expected, ListOrder.IsReorderable(JsonDocument.Parse(schema).RootElement));

    [Fact]
    public void A_schema_that_is_not_an_object_is_not_reorderable() =>
        Assert.False(ListOrder.IsReorderable(default));
}
