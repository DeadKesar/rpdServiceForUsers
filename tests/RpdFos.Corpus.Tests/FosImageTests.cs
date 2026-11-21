using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Corpus;
using Xunit;

namespace RpdFos.Corpus.Tests;

/// <summary>
/// Рисунки настоящих ФОС: формат, размер и место в тексте — по ним оценивается, что придётся принимать системе
/// после решения автора о вставке изображений. DOCX собираются тут же, файлов корпуса тесты не касаются.
/// </summary>
public class FosImageTests : IDisposable
{
    // PNG 1×1.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private const string Ns = """
        xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
        xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
        xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"
        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        """;

    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files) File.Delete(f);
    }

    /// <summary>Документ из одного абзаца с одним прогоном: <paramref name="runContent"/> получает идентификатор связи с картинкой PNG.</summary>
    private FosDoc ReadWith(Func<string, string> runContent)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fos-image-{Guid.NewGuid():N}.docx");
        _files.Add(path);
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var image = main.AddImagePart(ImagePartType.Png);
            image.FeedData(new MemoryStream(Png));
            var run = new Run($"<w:r {Ns}>{runContent(main.GetIdOfPart(image))}</w:r>");
            main.Document = new Document(new Body(new Paragraph(run)));
        }
        return FosReader.Read("T01", path, null);
    }

    private static string InlinePicture(string id, long cx, long cy) => $$"""
        <w:drawing><wp:inline><wp:extent cx="{{cx}}" cy="{{cy}}"/>
        <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
        <pic:pic><pic:blipFill><a:blip r:embed="{{id}}"/></pic:blipFill></pic:pic>
        </a:graphicData></a:graphic></wp:inline></w:drawing>
        """;

    [Fact]
    public void Inline_picture_gets_format_file_size_and_page_size()
    {
        var doc = ReadWith(id => InlinePicture(id, 2_160_000, 1_440_000));   // 6 × 4 см
        var image = Assert.Single(doc.Paragraphs.Single().Images);
        Assert.Equal(("картинка", "image/png", Png.Length, true), (image.Kind, image.MediaType, image.Bytes, image.Inline));
        Assert.Equal(6.0, image.WidthCm, 3);
        Assert.Equal(4.0, image.HeightCm, 3);
        Assert.True(image.Big);
        Assert.Equal(1, doc.Images);
    }

    [Fact]
    public void Small_picture_is_not_big()
    {
        var doc = ReadWith(id => InlinePicture(id, 720_000, 720_000));   // 2 × 2 см: значок или эмблема
        Assert.False(Assert.Single(doc.Paragraphs.Single().Images).Big);
        Assert.Equal((0, 1), (doc.Images, doc.SmallImages));
    }

    [Fact]
    public void Compatibility_copy_in_fallback_is_not_counted_twice()
    {
        // Word кладёт рядом с рисунком его копию для старых версий в mc:Fallback.
        var doc = ReadWith(id => $$"""
            <mc:AlternateContent>
              <mc:Choice Requires="wps">{{InlinePicture(id, 2_160_000, 1_440_000)}}</mc:Choice>
              <mc:Fallback><w:pict><v:shape style="width:170pt;height:113pt"><v:imagedata r:id="{{id}}"/></v:shape></w:pict></mc:Fallback>
            </mc:AlternateContent>
            """);
        Assert.Single(doc.Paragraphs.Single().Images);
    }

    [Fact]
    public void Vml_picture_takes_its_size_from_the_style()
    {
        var doc = ReadWith(id => $$"""
            <w:pict><v:shape style="position:absolute;width:6cm;height:3cm"><v:imagedata r:id="{{id}}"/></v:shape></w:pict>
            """);
        var image = Assert.Single(doc.Paragraphs.Single().Images);
        Assert.Equal(("image/png", false), (image.MediaType, image.Inline));
        Assert.Equal(6.0, image.WidthCm, 3);
    }

    [Fact]
    public void Equation_object_is_a_formula_and_other_ole_objects_are_pictures()
    {
        var doc = ReadWith(id => $$"""
            <w:object><v:shape style="width:30pt;height:12pt"><v:imagedata r:id="{{id}}"/></v:shape><o:OLEObject Type="Embed" ProgID="Equation.3" ShapeID="x" DrawAspect="Content" ObjectID="_1"/></w:object>
            <w:object><v:shape style="width:200pt;height:100pt"><v:imagedata r:id="{{id}}"/></v:shape><o:OLEObject Type="Embed" ProgID="Visio.Drawing.11" ShapeID="y" DrawAspect="Content" ObjectID="_2"/></w:object>
            """);
        var paragraph = doc.Paragraphs.Single();
        Assert.Equal(1, paragraph.Formulas);
        var image = Assert.Single(paragraph.Images);
        Assert.Equal("объект OLE: Visio.Drawing.11", image.Kind);
    }
}
