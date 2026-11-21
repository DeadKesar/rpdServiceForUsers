using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jint;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Логика таблиц редактора форматированного текста (этап 7, B4, доработка): строки и столбцы по сетке при
/// объединённых ячейках, вложенная таблица — текстом, совпадающим с санитайзером. Функции — чистые, без DOM,
/// запускаются в Jint: <c>wwwroot/js/rpdfos-table-logic.js</c> — тот же файл, что грузит браузер.
/// </summary>
/// <remarks>
/// Таблица в тестах — сетка: разделы по строкам <c>thead: …</c>, <c>tbody: …</c>, строки через «/», ячейки через
/// пробел; соседние одинаковые метки — одна объединённая ячейка (<c>a a</c> — по горизонтали, <c>a</c> над <c>a</c> —
/// по вертикали). В результате «+» — новая ячейка.
/// </remarks>
public class RichTextTableLogicTests
{
    private static readonly string Script =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "js", "rpdfos-table-logic.js"));

    private const string Plain = "tbody: a b c / d e f / g h i";
    private const string Header = "thead: h1 h2 h3\ntbody: a b c / d e f";
    private const string Wide = "tbody: a a b / c d e / f g h";
    private const string Tall = "tbody: a b c / a d e / f g h";
    private const string Block = "tbody: a a b / a a c / d e f";

    // ---- Сетка ----

    [Theory]
    [InlineData(Plain)]
    [InlineData(Header)]
    [InlineData(Wide)]
    [InlineData(Tall)]
    [InlineData(Block)]
    [InlineData("tbody: a b / a c")]
    [InlineData("thead: x x y / z w y\ntbody: a b c\ntfoot: i i i")]
    public void Grid_is_laid_out_as_the_browser_does(string table) =>
        // Ячейки строки — только собственные; объединённая сверху в строке не повторяется — и место ей даёт сетка.
        Assert.Equal(table, Render(Run(table, "gridOnly", "").Grid));

    // ---- Строка ниже и выше ----

    [Theory]
    [InlineData(Plain, "e", "tbody: a b c / d e f / + + + / g h i")]
    [InlineData(Plain, "i", "tbody: a b c / d e f / g h i / + + +")]
    [InlineData(Header, "h1", "thead: h1 h2 h3 / + + +\ntbody: a b c / d e f")]
    [InlineData(Header, "a", "thead: h1 h2 h3\ntbody: a b c / + + + / d e f")]
    // Ячейка объединена по строкам — строка встаёт под всей ячейкой.
    [InlineData(Tall, "a", "tbody: a b c / a d e / + + + / f g h")]
    [InlineData(Tall, "d", "tbody: a b c / a d e / + + + / f g h")]
    // Объединение через место вставки растягивается, в его столбцах новых ячеек нет.
    [InlineData(Tall, "b", "tbody: a b c / a + + / a d e / f g h")]
    [InlineData(Block, "b", "tbody: a a b / a a + / a a c / d e f")]
    [InlineData("thead: x x y / z w y\ntbody: a b c", "x", "thead: x x y / + + y / z w y\ntbody: a b c")]
    // Объединение по столбцам не мешает: в новой строке — по ячейке на каждый столбец.
    [InlineData(Wide, "a", "tbody: a a b / + + + / c d e / f g h")]
    public void Row_below_the_cell(string table, string cell, string expected) =>
        Assert.Equal(expected, Render(Run(table, "insertRow", cell, after: true).Grid));

    [Theory]
    [InlineData(Plain, "e", "tbody: a b c / + + + / d e f / g h i")]
    [InlineData(Plain, "a", "tbody: + + + / a b c / d e f / g h i")]
    [InlineData(Header, "h2", "thead: + + + / h1 h2 h3\ntbody: a b c / d e f")]
    [InlineData(Tall, "a", "tbody: + + + / a b c / a d e / f g h")]
    [InlineData(Tall, "d", "tbody: a b c / a + + / a d e / f g h")]
    [InlineData(Tall, "g", "tbody: a b c / a d e / + + + / f g h")]
    [InlineData(Block, "e", "tbody: a a b / a a c / + + + / d e f")]
    public void Row_above_the_cell(string table, string cell, string expected) =>
        Assert.Equal(expected, Render(Run(table, "insertRow", cell, after: false).Grid));

    // ---- Столбец справа и слева ----

    [Theory]
    [InlineData(Plain, "e", "tbody: a b + c / d e + f / g h + i")]
    [InlineData(Plain, "c", "tbody: a b c + / d e f + / g h i +")]
    [InlineData(Header, "h2", "thead: h1 h2 + h3\ntbody: a b + c / d e + f")]
    // Ячейка по столбцам: справа от неё, а не посередине.
    [InlineData(Wide, "a", "tbody: a a + b / c d + e / f g + h")]
    [InlineData(Wide, "d", "tbody: a a + b / c d + e / f g + h")]
    // Объединение через место вставки растягивается и в нём нового столбца нет.
    [InlineData(Wide, "c", "tbody: a a a b / c + d e / f + g h")]
    [InlineData(Block, "e", "tbody: a a + b / a a + c / d e + f")]
    [InlineData(Block, "a", "tbody: a a + b / a a + c / d e + f")]
    [InlineData(Tall, "b", "tbody: a b + c / a d + e / f g + h")]
    public void Column_right_of_the_cell(string table, string cell, string expected) =>
        Assert.Equal(expected, Render(Run(table, "insertColumn", cell, after: true).Grid));

    [Theory]
    [InlineData(Plain, "e", "tbody: a + b c / d + e f / g + h i")]
    [InlineData(Plain, "a", "tbody: + a b c / + d e f / + g h i")]
    [InlineData(Header, "h1", "thead: + h1 h2 h3\ntbody: + a b c / + d e f")]
    [InlineData(Wide, "d", "tbody: a a a b / c + d e / f + g h")]
    [InlineData(Wide, "a", "tbody: + a a b / + c d e / + f g h")]
    [InlineData(Block, "e", "tbody: a a a b / a a a c / d + e f")]
    [InlineData(Tall, "d", "tbody: a + b c / a + d e / f + g h")]
    public void Column_left_of_the_cell(string table, string cell, string expected) =>
        Assert.Equal(expected, Render(Run(table, "insertColumn", cell, after: false).Grid));

    // ---- Удаление строки ----

    [Theory]
    [InlineData(Plain, "e", "tbody: a b c / g h i")]
    [InlineData(Plain, "a", "tbody: d e f / g h i")]
    [InlineData(Plain, "i", "tbody: a b c / d e f")]
    // Строка заголовка уходит вместе со своим разделом.
    [InlineData(Header, "h1", "tbody: a b c / d e f")]
    // Единственная строка тела не исчезает — остаётся пустая.
    [InlineData("thead: h1 h2\ntbody: a b", "a", "thead: h1 h2\ntbody: + +")]
    // Объединение, начатое выше, сужается.
    [InlineData(Tall, "d", "tbody: a b c / f g h")]
    // Объединение, начатое в удаляемой строке, переходит в следующую вместе со своим содержимым.
    [InlineData(Tall, "b", "tbody: a d e / f g h")]
    [InlineData(Block, "b", "tbody: a a c / d e f")]
    [InlineData("tbody: a b c d / e b f d / g h i j", "a", "tbody: e b f d / g h i j")]
    [InlineData("tbody: a b c d / e b f d / g h i j", "c", "tbody: e b f d / g h i j")]
    // Удаляются все строки, которые занимает ячейка.
    [InlineData(Tall, "a", "tbody: f g h")]
    [InlineData(Block, "a", "tbody: d e f")]
    [InlineData("tbody: a b c / a d e / a f g / h i j", "a", "tbody: h i j")]
    [InlineData("tbody: a b c / a d e / a f g / h i j", "d", "tbody: a b c / a f g / h i j")]
    [InlineData("thead: x x y / z w y\ntbody: a b c", "y", "tbody: a b c")]
    public void Delete_rows_of_the_cell(string table, string cell, string expected) =>
        Assert.Equal(expected, Render(Run(table, "deleteRows", cell).Grid));

    // ---- Удаление столбца ----

    [Theory]
    [InlineData(Plain, "e", "tbody: a c / d f / g i")]
    [InlineData(Plain, "a", "tbody: b c / e f / h i")]
    [InlineData(Plain, "c", "tbody: a b / d e / g h")]
    [InlineData(Header, "h2", "thead: h1 h3\ntbody: a c / d f")]
    // Ячейка, занимающая и другие столбцы, сужается, а не исчезает.
    [InlineData(Wide, "c", "tbody: a b / d e / g h")]
    [InlineData(Wide, "d", "tbody: a b / c e / f h")]
    [InlineData(Block, "d", "tbody: a b / a c / e f")]
    [InlineData(Tall, "d", "tbody: a c / a e / f h")]
    // Удаляются все столбцы, которые занимает ячейка.
    [InlineData(Wide, "a", "tbody: b / e / h")]
    [InlineData(Block, "a", "tbody: b / c / f")]
    [InlineData("tbody: a b b c / d d e e / f g h i", "b", "tbody: a c / d e / f i")]
    [InlineData("thead: x x y\ntbody: a b c / d e f", "x", "thead: y\ntbody: c / f")]
    public void Delete_columns_of_the_cell(string table, string cell, string expected) =>
        Assert.Equal(expected, Render(Run(table, "deleteColumns", cell).Grid));

    [Theory]
    [InlineData("tbody: a / b")]
    [InlineData("tbody: a a / b c")]
    [InlineData("thead: h h\ntbody: a b")]
    public void The_last_column_stays(string table)
    {
        var run = Run(table, "deleteColumns", table.Contains("h h") ? "h" : "a");

        Assert.Equal("last-column", run.Reason);
        Assert.Equal(table, Render(run.Grid));
    }

    // ---- Прочее ----

    [Fact]
    public void Unknown_cell_changes_nothing()
    {
        var run = Run(Plain, "deleteRows", "нет такой");

        Assert.Equal("no-cell", run.Reason);
        Assert.Equal(Plain, Render(run.Grid));
    }

    [Fact]
    public void New_cells_follow_their_section_and_are_not_merged()
    {
        // Заголовок — th, остальное — td; у новой ячейки нет объединения.
        var run = Run(Header, "insertRow", "h1", after: true);
        var head = run.Model["sections"]![0]!["rows"]![1]!["cells"]!.AsArray();
        var column = Run(Header, "insertColumn", "a", after: true).Model["sections"]!.AsArray();

        Assert.All(head, c => Assert.Equal("th", (string?)c!["tag"]));
        Assert.All(head, c => Assert.Equal((1, 1), ((int)c!["colspan"]!, (int)c["rowspan"]!)));
        Assert.Equal("th", (string?)column[0]!["rows"]![0]!["cells"]![2]!["tag"]);
        Assert.Equal("td", (string?)column[1]!["rows"]![0]!["cells"]![2]!["tag"]);
    }

    [Fact]
    public void Merging_that_no_longer_merges_loses_its_attribute_value()
    {
        // Объединение, сузившееся до одного места, — обычная ячейка: адаптер снимет colspan и rowspan.
        var run = Run(Wide, "deleteColumns", "c");

        Assert.Equal(1, (int)run.Model["sections"]![0]!["rows"]![0]!["cells"]![0]!["colspan"]!);
    }

    // ---- Вложенная таблица — текстом, как у санитайзера ----

    [Theory]
    [MemberData(nameof(RichTextSanitizerTests.NestedTableMarkups), MemberType = typeof(RichTextSanitizerTests))]
    public void Nested_table_becomes_the_text_the_sanitizer_keeps(string html, string expected)
    {
        // Редактор делает вложенную таблицу текстом сразу, не дожидаясь сохранения: результат — тот же.
        Assert.Equal(expected, FlattenNested(html));
        Assert.Equal(expected, new RichTextSanitizer().Sanitize(html));
    }

    [Fact]
    public void Flatten_reports_how_many_tables_it_turned_into_text()
    {
        const string html = "<table><tr><td><table><tr><td>а</td></tr></table>и<table><tr><td>б</td></tr></table></td></tr></table>";
        var body = new HtmlParser().ParseDocument("").Body!;
        body.InnerHtml = html;
        var engine = new Engine().SetValue("treeJson", Tree(body).ToJsonString());
        engine.Execute(Script);

        var count = engine.Evaluate("RpdFosTableLogic.flattenNested(JSON.parse(treeJson))").AsNumber();

        Assert.Equal(2, count);
    }

    [Fact]
    public void Table_outside_cells_is_not_touched()
    {
        // Внешняя таблица — не вложенная: ни одна функция её не разбирает.
        const string html = "<p>до</p><table><tbody><tr><td>x</td><td>y</td></tr></tbody></table>после";

        Assert.Equal(html, FlattenNested(html));
    }

    // ---- Помощники ----

    private record Result(string? Reason, JsonNode Grid, JsonObject Model);

    private static Result Run(string table, string operation, string cell, bool after = false)
    {
        var engine = new Engine();
        engine.Execute(Script);
        engine.SetValue("modelJson", ModelJson(table)).SetValue("operation", operation).SetValue("cellId", cell).SetValue("after", after);
        var json = engine.Evaluate(
            """
            (function () {
              var model = JSON.parse(modelJson);
              var result = operation === 'gridOnly' ? { ok: true } : RpdFosTableLogic[operation](model, cellId, after);
              return JSON.stringify({ ok: result.ok, reason: result.reason, grid: RpdFosTableLogic.gridOf(model), model: model });
            })()
            """).AsString();
        var answer = JSON.Parse(json);
        return new Result((string?)answer["reason"], answer["grid"]!, answer["model"]!.AsObject());
    }

    // Из сетки — в модель: ячейка — прямоугольник из одинаковых меток; в строке — только те ячейки, что в ней начаты.
    private static string ModelJson(string table)
    {
        var sections = new JsonArray();
        foreach (var line in table.Split('\n'))
        {
            var colon = line.IndexOf(':');
            var kind = line[..colon];
            var rows = line[(colon + 1)..].Split('/').Select(r => r.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            var claimed = new bool[rows.Length, rows.Max(r => r.Length)];
            var modelRows = new JsonArray();
            for (var r = 0; r < rows.Length; r++)
            {
                var cells = new JsonArray();
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (claimed[r, c]) continue;
                    var label = rows[r][c];
                    int colspan = 1, rowspan = 1;
                    while (c + colspan < rows[r].Length && !claimed[r, c + colspan] && rows[r][c + colspan] == label) colspan++;
                    while (r + rowspan < rows.Length && Enumerable.Range(c, colspan).All(x => x < rows[r + rowspan].Length && rows[r + rowspan][x] == label)) rowspan++;
                    for (var y = 0; y < rowspan; y++)
                        for (var x = 0; x < colspan; x++) claimed[r + y, c + x] = true;
                    cells.Add(new JsonObject { ["id"] = label, ["tag"] = kind == "thead" ? "th" : "td", ["colspan"] = colspan, ["rowspan"] = rowspan });
                }
                modelRows.Add(new JsonObject { ["cells"] = cells });
            }
            sections.Add(new JsonObject { ["kind"] = kind, ["rows"] = modelRows });
        }
        return new JsonObject { ["sections"] = sections }.ToJsonString();
    }

    // Из ответа сценария — в ту же запись: «thead: a b / c d» по разделам.
    private static string Render(JsonNode grid) => string.Join("\n", grid.AsArray().Select(section =>
        $"{(string?)section!["kind"]}: " + string.Join(" / ", section["rows"]!.AsArray().Select(row =>
            string.Join(" ", row!.AsArray().Select(x => (string?)x))))));

    private static string FlattenNested(string html)
    {
        // Разбор — как у санитайзера: в элемент body, до разбора ячеек и обязательного tbody.
        var body = new HtmlParser().ParseDocument("").Body!;
        body.InnerHtml = html;
        var engine = new Engine().SetValue("treeJson", Tree(body).ToJsonString());
        engine.Execute(Script);
        return engine.Evaluate(
            """
            (function () {
              var tree = JSON.parse(treeJson);
              RpdFosTableLogic.flattenNested(tree);
              RpdFosTableLogic.removeLayoutSpace(tree);
              return RpdFosTableLogic.serializeChildren(tree);
            })()
            """).AsString();
    }

    // Дерево для сценария: элемент — { tag, attrs, children }, текст — { text }; остальные узлы в разметке не нужны.
    private static JsonNode Tree(INode node) => node switch
    {
        IElement element => new JsonObject
        {
            ["tag"] = element.LocalName,
            ["attrs"] = new JsonObject(element.Attributes.Select(a => KeyValuePair.Create(a.Name, (JsonNode?)a.Value))),
            ["children"] = new JsonArray(element.ChildNodes.Where(n => n is IElement or IText).Select(Tree).ToArray())
        },
        IText text => new JsonObject { ["text"] = text.Data },
        _ => throw new NotSupportedException()
    };

    private static class JSON
    {
        public static JsonNode Parse(string json) => JsonNode.Parse(json)!;
    }
}
