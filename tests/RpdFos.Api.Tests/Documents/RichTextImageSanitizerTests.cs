using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Рисунки в форматированном тексте (этап 7, B4а; NFR-SEC-10): остаётся только свой адрес, размер — числами.</summary>
public class RichTextImageSanitizerTests
{
    private static readonly RichTextSanitizer Sanitizer = new();
    private static readonly Guid Id = Guid.Parse("3f2b8c1e-6d4a-4f0e-9b7a-1c2d3e4f5a6b");
    private static readonly string Own = $"/api/images/{Id}";

    [Fact]
    public void Own_image_is_kept_with_size_and_description()
    {
        var html = $"<p>Схема цепи:</p><p><img src=\"{Own}\" width=\"320\" height=\"180\" alt=\"Схема\"></p>";

        Assert.Equal(html, Sanitizer.Sanitize(html));
    }

    [Fact]
    public void Image_in_a_line_and_in_a_table_cell_is_kept()
    {
        var html = $"<p>до <img src=\"{Own}\"> после</p><table><tbody><tr><td><img src=\"{Own}\" width=\"50\"></td><td>текст</td></tr></tbody></table>";

        Assert.Equal(html, Sanitizer.Sanitize(html));
    }

    [Theory]
    [InlineData("https://example.org/a.png")]
    [InlineData("//example.org/a.png")]
    [InlineData("http://localhost/api/images/3f2b8c1e-6d4a-4f0e-9b7a-1c2d3e4f5a6b")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("file:///C:/Users/Ivanov/Pictures/scheme.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/api/images/not-a-guid")]
    [InlineData("/api/images/3f2b8c1e-6d4a-4f0e-9b7a-1c2d3e4f5a6b/../../secret")]
    [InlineData("/api/images/3f2b8c1e-6d4a-4f0e-9b7a-1c2d3e4f5a6b?x=1")]
    [InlineData("/api/documents/3f2b8c1e-6d4a-4f0e-9b7a-1c2d3e4f5a6b/images")]
    [InlineData("")]
    public void Image_with_a_foreign_or_malformed_address_is_removed(string source) =>
        Assert.Equal("<p>до  после</p>", Sanitizer.Sanitize($"<p>до <img src=\"{source}\" alt=\"x\" width=\"10\"> после</p>"));

    [Fact]
    public void Image_without_source_is_removed()
    {
        Assert.Equal("<p>текст</p>", Sanitizer.Sanitize("<p>текст<img alt=\"нет адреса\"></p>"));
    }

    [Fact]
    public void Other_attributes_of_the_image_are_dropped()
    {
        var html = $"<img src=\"{Own}\" onerror=\"alert(1)\" onload=\"x()\" style=\"width:9999px\" class=\"c\" srcset=\"https://example.org/a.png 2x\" " +
                   "loading=\"lazy\" id=\"i\" title=\"t\" data-x=\"1\" width=\"100\">";

        Assert.Equal($"<img src=\"{Own}\" width=\"100\">", Sanitizer.Sanitize(html));
    }

    [Theory]
    [InlineData("width=\"100%\"", "")]
    [InlineData("width=\"12px\"", "")]
    [InlineData("width=\"-5\"", "")]
    [InlineData("width=\"0\"", "")]
    [InlineData("width=\"100000\"", "")]
    [InlineData("width=\"1e3\"", "")]
    [InlineData("width=\"٣٠٠\"", "")]
    [InlineData("width=\"10000\" height=\"1\"", " width=\"10000\" height=\"1\"")]
    [InlineData("width=\"0300\"", " width=\"300\"")]
    public void Size_is_kept_only_as_a_whole_number_in_range(string attributes, string expected) =>
        Assert.Equal($"<img src=\"{Own}\"{expected}>", Sanitizer.Sanitize($"<img src=\"{Own}\" {attributes}>"));

    [Fact]
    public void Width_and_height_of_other_elements_are_dropped()
    {
        Assert.Equal("<table><tbody><tr><td>а</td></tr></tbody></table><p>б</p>",
            Sanitizer.Sanitize("<table width=\"100%\" height=\"5\"><tbody><tr><td width=\"20\" height=\"3\">а</td></tr></tbody></table><p width=\"3\">б</p>"));
    }

    [Fact]
    public void Description_is_text_and_cut_to_the_limit()
    {
        var sanitized = Sanitizer.Sanitize($"<img src=\"{Own}\" alt=\"&lt;script&gt;alert(1)&lt;/script&gt; &quot;кавычки&quot;\">");
        var long_ = Sanitizer.Sanitize($"<img src=\"{Own}\" alt=\"{new string('я', 700)}\">")!;

        Assert.DoesNotMatch("alt=\"[^\"]*[<>]", sanitized);
        Assert.Contains("script", sanitized);
        Assert.Equal(500, long_.Split("alt=\"")[1].TrimEnd('"', '>').Length);
        Assert.Equal($"<img src=\"{Own}\">", Sanitizer.Sanitize($"<img src=\"{Own}\" alt=\"   \">"));
    }

    [Fact]
    public void Address_is_normalized_to_the_lower_case_form()
    {
        Assert.Equal($"<img src=\"{Own}\">", Sanitizer.Sanitize($"<img src=\"{Own.ToUpperInvariant()}\">".Replace("/API/IMAGES/", "/api/images/")));
    }

    [Fact]
    public void Only_images_the_caller_allows_stay()
    {
        var other = Guid.Parse("aaaaaaaa-6d4a-4f0e-9b7a-1c2d3e4f5a6b");
        var html = $"<p><img src=\"{Own}\"><img src=\"/api/images/{other}\"></p>";

        Assert.Equal($"<p><img src=\"{Own}\"></p>", Sanitizer.Sanitize(html, id => id == Id));
        Assert.Equal("<p></p>", Sanitizer.Sanitize(html, _ => false));
        Assert.Equal(html, Sanitizer.Sanitize(html));
    }

    [Fact]
    public void Sanitizing_twice_changes_nothing()
    {
        var once = Sanitizer.Sanitize($"<p>а <img src=\"{Own}\" width=\"0300\" alt=\"  схема \"></p><img src=\"https://example.org/a.png\">")!;

        Assert.Equal(once, Sanitizer.Sanitize(once));
    }

    [Fact]
    public void Image_ids_are_read_from_markup_and_from_content()
    {
        var other = Guid.Parse("aaaaaaaa-6d4a-4f0e-9b7a-1c2d3e4f5a6b");
        var content = System.Text.Json.Nodes.JsonNode.Parse(
            $$"""{ "goals": "<p><img src=\"/api/images/{{Id}}\"></p>", "list": [ "x", "<img src=\"/api/images/{{other}}\">" ], "n": 5 }""");

        Assert.Equal([Id, other], DocumentImages.IdsIn(content).OrderBy(i => i == Id ? 0 : 1));
        Assert.Equal([Id], DocumentImages.IdsIn($"<img src=\"{Own}\">"));
        Assert.Empty(DocumentImages.IdsIn("<img src=\"/api/images/xyz\">"));
    }
}
