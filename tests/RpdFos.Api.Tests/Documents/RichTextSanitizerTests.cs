using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Санитайзер форматированного текста (NFR-SEC-10): белый список элементов без атрибутов.</summary>
public class RichTextSanitizerTests
{
    private static readonly RichTextSanitizer Sanitizer = new();

    [Fact]
    public void Allowed_markup_is_kept()
    {
        const string html = "<p>Текст <strong>полужирный</strong>, <b>b</b>, <em>курсив</em>, <i>i</i><br>строка</p>" +
                            "<ul><li>пункт</li></ul><ol><li>первый<ol><li>вложенный</li></ol></li></ol>";

        Assert.Equal(html, Sanitizer.Sanitize(html));
    }

    [Theory]
    [InlineData("<script>alert(1)</script><p>ok</p>", "<p>ok</p>")]
    [InlineData("<p onclick=\"steal()\" onmouseover=\"x()\">t</p>", "<p>t</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">ссылка</a>", "ссылка")]
    [InlineData("<p style=\"color:red; background:url(javascript:x)\" class=\"c\" id=\"i\">t</p>", "<p>t</p>")]
    [InlineData("<style>p { color: red }</style><p>s</p>", "<p>s</p>")]
    [InlineData("<img src=x onerror=alert(1)>текст", "текст")]
    [InlineData("<iframe src=\"https://example.org\"></iframe><svg><script>alert(2)</script></svg>x", "x")]
    [InlineData("<strong onclick=\"x()\"><em style=\"x\">ж</em></strong>", "<strong><em>ж</em></strong>")]
    public void Dangerous_markup_is_removed(string html, string expected) =>
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    [Theory]
    [InlineData("<div>текст <span style=\"font-size:20pt\">из Word</span></div>", "текст из Word")]
    [InlineData("<h1>Заголовок</h1><p>абзац</p>", "Заголовок<p>абзац</p>")]
    // Таблица остаётся таблицей (этап 7, B4); подпись и группы столбцов — не её содержимое, текст подписи сохраняется.
    [InlineData("<table><caption>Подпись</caption><colgroup><col></colgroup><tr><td>ячейка</td></tr></table>",
        "Подпись<table><tbody><tr><td>ячейка</td></tr></tbody></table>")]
    public void Other_markup_is_unwrapped_keeping_text(string html, string expected) =>
        // Вставка из Word не теряет слов: снимается разметка, текст остаётся.
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    [Fact]
    public void Escaped_text_stays_text() =>
        Assert.Equal("<p>a &lt;script&gt; &amp; b</p>", Sanitizer.Sanitize("<p>a &lt;script&gt; &amp; b</p>"));

    [Theory]
    // Разметка редактора Radzen (Ctrl+B, Ctrl+I, список) — как она пришла из браузера при проверке этапа 4.
    [InlineData("Сформировать у обучающихся <span style=\"font-weight: bold;\">умения</span> строить модели.",
        "Сформировать у обучающихся <strong>умения</strong> строить модели.")]
    [InlineData("<div><ul><li>моделирование<span style=\"font-style: italic;\"> (курсив)</span></li></ul></div>",
        "<ul><li>моделирование<em> (курсив)</em></li></ul>")]
    [InlineData("<span style=\"font-weight: 700; font-style: italic; color: red\">оба</span>", "<em><strong>оба</strong></em>")]
    [InlineData("<span style=\"color: red\">цвет</span>", "цвет")]
    public void Bold_and_italic_styles_become_tags(string html, string expected) =>
        // Без перевода в теги санитайзер снял бы span со стилем, и форматирование молча пропало бы.
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    // Таблицы (этап 7, B4): строки, ячейки, объединение — в белом списке; всё остальное снимается, как у прочего текста.

    [Fact]
    public void Table_with_row_groups_and_merged_cells_is_kept()
    {
        const string html = "<table><thead><tr><th>Вход</th><th colspan=\"2\">Выход</th></tr></thead>" +
                            "<tbody><tr><td rowspan=\"2\">a</td><td>b</td><td>c</td></tr><tr><td>d</td><td>e</td></tr></tbody>" +
                            "<tfoot><tr><td colspan=\"3\">итого</td></tr></tfoot></table>";

        Assert.Equal(html, Sanitizer.Sanitize(html));
    }

    [Fact]
    public void Table_gets_tbody_like_in_a_browser() =>
        Assert.Equal("<table><tbody><tr><td>ячейка</td></tr></tbody></table>",
            Sanitizer.Sanitize("<table><tr><td>ячейка</td></tr></table>"));

    [Fact]
    public void Cell_keeps_paragraphs_bold_italic_and_lists()
    {
        const string html = "<table><tbody><tr><td><p>Текст <strong>жирный</strong> и <em>курсив</em></p><ul><li>пункт</li></ul>" +
                            "строка<br>ещё</td></tr></tbody></table>";

        Assert.Equal(html, Sanitizer.Sanitize(html));
    }

    [Theory]
    [InlineData("<table border=\"1\" width=\"100%\" style=\"width:100%\" class=\"MsoTable\" id=\"t\" onclick=\"x()\">" +
                "<tr style=\"height:15pt\" onmouseover=\"y()\"><td style=\"background:red\" width=\"50%\" valign=\"top\" " +
                "align=\"center\" bgcolor=\"red\" data-x=\"1\" onclick=\"z()\">т</td></tr></table>",
        "<table><tbody><tr><td>т</td></tr></tbody></table>")]
    [InlineData("<table background=\"javascript:alert(1)\"><tr><td background=\"x\"><script>alert(1)</script>т" +
                "<img src=x onerror=alert(1)><svg><script>alert(2)</script></svg></td></tr></table>",
        "<table><tbody><tr><td>т</td></tr></tbody></table>")]
    public void Table_attributes_outside_the_list_are_removed(string html, string expected) =>
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    [Theory]
    [InlineData("td", "colspan=\"2\"", " colspan=\"2\"")]
    [InlineData("th", "rowspan=\"3\"", " rowspan=\"3\"")]
    [InlineData("td", "colspan=\"100\"", " colspan=\"100\"")]
    [InlineData("td", "colspan=\"02\"", " colspan=\"2\"")]
    [InlineData("td", "colspan=\"1\"", "")]
    [InlineData("td", "colspan=\"0\"", "")]
    [InlineData("td", "colspan=\"-2\"", "")]
    [InlineData("td", "colspan=\"101\"", "")]
    [InlineData("td", "colspan=\"99999999999\"", "")]
    [InlineData("td", "rowspan=\"2px\"", "")]
    [InlineData("td", "rowspan=\"2;alert(1)\"", "")]
    [InlineData("td", "colspan=\"\"", "")]
    [InlineData("td", "rowspan=\"1e1\"", "")]
    [InlineData("td", "colspan=\"٢\"", "")]
    [InlineData("td", "colspan", "")]
    public void Merged_cells_take_only_plain_numbers(string tag, string attribute, string expected) =>
        Assert.Equal($"<table><tbody><tr><{tag}{expected}>x</{tag}></tr></tbody></table>",
            Sanitizer.Sanitize($"<table><tr><{tag} {attribute}>x</{tag}></tr></table>"));

    [Theory]
    [InlineData("<p colspan=\"2\" rowspan=\"2\">t</p>", "<p>t</p>")]
    [InlineData("<table colspan=\"2\" rowspan=\"2\"><tr colspan=\"2\"><td>t</td></tr></table>", "<table><tbody><tr><td>t</td></tr></tbody></table>")]
    [InlineData("<ul rowspan=\"2\"><li colspan=\"2\"><strong colspan=\"2\">t</strong></li></ul>", "<ul><li><strong>t</strong></li></ul>")]
    public void Merging_outside_cells_is_removed(string html, string expected) =>
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    /// <summary>
    /// Вложенная таблица — текст: строки через перенос, ячейки через пробел, от текста вокруг отделена переносом.
    /// Те же разметки проверяет RichTextTableLogicTests: редактор делает вложенную таблицу текстом сразу, и результат
    /// должен совпадать с тем, что сохранит сервер.
    /// </summary>
    public static IEnumerable<object[]> NestedTableMarkups =>
    [
        ["<table><tr><td>до<table><tr><td>а</td><td>б</td></tr><tr><td><p>в</p></td><td>г</td></tr></table>после</td></tr></table>",
            "<table><tbody><tr><td>до<br>а б<br>в г<br>после</td></tr></tbody></table>"],
        ["<table><tr><td><table><tr><td>а</td><td>б</td></tr></table></td><td>в</td></tr></table>",
            "<table><tbody><tr><td>а б</td><td>в</td></tr></tbody></table>"],
        // Два уровня вложенности — текстом оба.
        ["<table><tr><td><table><tr><td>а<table><tr><td>б</td></tr></table></td></tr></table></td></tr></table>",
            "<table><tbody><tr><td>а<br>б</td></tr></tbody></table>"],
        // Отступы и переводы строк между разметкой — как у таблицы из Word.
        ["<table>\n <tr>\n  <td>\n   <table>\n    <tr>\n     <td>а</td>\n     <td>б</td>\n    </tr>\n   </table>\n  </td>\n </tr>\n</table>",
            "<table><tbody><tr><td>а б</td></tr></tbody></table>"],
        // Абзацы вокруг вложенной таблицы: перенос с каждой стороны.
        ["<table><tr><td><p>до</p><table><tr><td>x</td></tr></table><p>после</p></td></tr></table>",
            "<table><tbody><tr><td><p>до</p><br>x<br><p>после</p></td></tr></tbody></table>"],
        // Ячейка заголовка и полужирный в ячейке.
        ["<table><tr><td><table><tr><th><b>а</b></th><td>б</td></tr></table></td></tr></table>",
            "<table><tbody><tr><td><b>а</b> б</td></tr></tbody></table>"],
        // Две вложенные таблицы подряд в одной ячейке.
        ["<table><tr><td><table><tr><td>а</td></tr></table><table><tr><td>б</td></tr></table></td></tr></table>",
            "<table><tbody><tr><td>а<br>б</td></tr></tbody></table>"],
        // Вложенная таблица в пункте списка внутри ячейки.
        ["<table><tr><td><ul><li>пункт<table><tr><td>y</td></tr></table></li></ul></td></tr></table>",
            "<table><tbody><tr><td><ul><li>пункт<br>y</li></ul></td></tr></tbody></table>"]
    ];

    [Theory]
    [MemberData(nameof(NestedTableMarkups))]
    public void Nested_table_stays_as_text(string html, string expected) =>
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    [Fact]
    public void Table_pasted_from_word_keeps_structure_and_text()
    {
        // Как Word кладёт таблицу в буфер: классы Mso, стили, пустые o:p, отступы и переводы строк между разметкой.
        const string word =
            "<table class=MsoTableGrid border=1 cellspacing=0 cellpadding=0 style='border-collapse:collapse;border:none'>\r\n" +
            " <tr style='height:15.0pt'>\r\n" +
            "  <td width=319 valign=top style='width:239.4pt;border:solid windowtext 1.0pt'>\r\n" +
            "  <p class=MsoNormal><b><span style='font-size:12.0pt'>Вход<o:p></o:p></span></b></p>\r\n" +
            "  </td>\r\n" +
            "  <td width=319 colspan=2 valign=top style='border:solid windowtext 1.0pt'>\r\n" +
            "  <p class=MsoNormal><span style='font-size:12.0pt'>Выход <i>x</i> <o:p></o:p></span></p>\r\n" +
            "  </td>\r\n" +
            " </tr>\r\n" +
            "</table><p class=MsoNormal><o:p>&nbsp;</o:p></p>";

        Assert.Equal("<table><tbody><tr><td><p><b>Вход</b></p></td><td colspan=\"2\"><p>Выход <i>x</i> </p></td></tr></tbody></table>" +
                     "<p>&nbsp;</p>", Sanitizer.Sanitize(word));
    }

    [Fact]
    public void Table_inserted_by_the_editor_loses_only_styles()
    {
        // Так выглядит вставка таблицы в редакторе Radzen: стили и рамка — на элементах, неразрывный пробел — в пустых
        // ячейках (иначе в них не встать курсором), перенос после таблицы — для строки под ней.
        const string inserted =
            "<table style=\"width: 100%; border-collapse: collapse;\" border=\"1\"><thead><tr><th>Столбец 1</th><th>Столбец 2</th></tr></thead>" +
            "<tbody><tr><td style=\"width: 50%;\">&nbsp;</td><td>&nbsp;</td></tr></tbody></table><br>";

        Assert.Equal("<table><thead><tr><th>Столбец 1</th><th>Столбец 2</th></tr></thead>" +
                     "<tbody><tr><td>&nbsp;</td><td>&nbsp;</td></tr></tbody></table><br>", Sanitizer.Sanitize(inserted));
    }

    [Fact]
    public void Text_inside_a_table_but_outside_cells_is_not_lost() =>
        // Разбор HTML выносит такой текст перед таблицей — слова остаются.
        Assert.Equal("потерянное<table><tbody><tr><td>x</td></tr></tbody></table>",
            Sanitizer.Sanitize("<table>потерянное<tr><td>x</td></tr></table>"));

    [Theory]
    [InlineData("<table><tr><td><p>a</p> <p>b</p></td><td> <b>x</b> <i>y</i> </td></tr></table>")]
    [InlineData("<table><thead><tr><th colspan=\"2\">a</th></tr></thead><tr><td>b</td><td rowspan=\"2\">c</td></tr><tr><td>d</td></tr></table>")]
    [InlineData("<table><tr><td>до<table><tr><td>а</td><td>б</td></tr></table>после</td></tr></table>")]
    [InlineData("<p>до</p><table><tr><td>x</td></tr></table>после<ul><li>пункт<table><tr><td>y</td></tr></table></li></ul>")]
    [InlineData("<p>абзац<table><tr><td>x</td></tr></table>продолжение</p>")]
    public void Sanitizing_twice_changes_nothing(string html)
    {
        // Содержимое санитизируется при каждом сохранении: результат не должен меняться от повторного прохода.
        var once = Sanitizer.Sanitize(html);

        Assert.Equal(once, Sanitizer.Sanitize(once));
    }
}
