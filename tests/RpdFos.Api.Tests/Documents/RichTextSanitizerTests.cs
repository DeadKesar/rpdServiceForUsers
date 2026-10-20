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
    [InlineData("<table><tr><td>ячейка</td></tr></table>", "ячейка")]
    [InlineData("<h1>Заголовок</h1><p>абзац</p>", "Заголовок<p>абзац</p>")]
    public void Other_markup_is_unwrapped_keeping_text(string html, string expected) =>
        // Вставка из Word не теряет слов: снимается разметка, текст остаётся.
        Assert.Equal(expected, Sanitizer.Sanitize(html));

    [Fact]
    public void Escaped_text_stays_text() =>
        Assert.Equal("<p>a &lt;script&gt; &amp; b</p>", Sanitizer.Sanitize("<p>a &lt;script&gt; &amp; b</p>"));
}
