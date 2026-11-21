using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>
/// Рисунки форматированного текста в печати (этап 7, B4а): рисунок DOCX заданного размера, не шире места, пропорции и описание
/// сохраняются, файл проходит OpenXmlValidator. Без базы: маленький шаблон собирает сам тест, рисунки подставляются в контекст.
/// </summary>
public class RichTextImagePrintingTests
{
    private static readonly DocumentTypeRegistry Registry = new();
    private static readonly Guid Scheme = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Graph = Guid.Parse("66666666-7777-4888-9999-000000000000");

    // Страница A4 с полями 3 и 1,5 см: текст — 9355 twips.
    private const int TextWidthDxa = 9355;
    private const long Emu = 9525, DxaEmu = 635;

    private static string Img(Guid id, string attributes = "") => $"<img src=\"/api/images/{id}\" {attributes}>";

    private static PrintContext Context(string html, IReadOnlyDictionary<Guid, ImageInfo> images)
    {
        const string schemaJson = """{ "type": "object", "properties": { "text": { "type": "string", "x-ui-widget": "richtext" } } }""";
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { text = html })).RootElement;
        var facts = new DisciplineFacts(
            new Discipline { Name = "Дисциплина", Semesters = [new DisciplineSemesterDetails { Semester = 1, Lectures = 16, ControlTypes = ["exam"] }] },
            new CurriculumPlan(), new StudyDirection(), new StudyProfile(), null, []);
        return new PrintContext(payload, schema, Registry.FindSchema, facts, new SignatoryReport([], []), 2026,
            DocumentMetadata.Hidden(schema, facts.Semesters, Registry.FindSchema), images);
    }

    private static readonly Dictionary<string, BookmarkTarget> Mapping = new() { ["AutofillText"] = new(BookmarkTargetKinds.Field, "/text") };

    private static ImageInfo Info(byte[] content) => ImageFiles.Inspect(content).Image!;

    private static IReadOnlyDictionary<Guid, ImageInfo> Images() => new Dictionary<Guid, ImageInfo>
    {
        [Scheme] = Info(TestImages.Png(200, 100, seed: 1)), [Graph] = Info(TestImages.Jpeg(40, 30, seed: 2))
    };

    private static Paragraph Bookmarked(string before = "")
    {
        var paragraph = new Paragraph();
        if (before.Length > 0) paragraph.AppendChild(new Run(new Text(before) { Space = SpaceProcessingModeValues.Preserve }));
        paragraph.Append(new BookmarkStart { Name = "AutofillText1", Id = "1" }, new Run(new Text("AutofillText")), new BookmarkEnd { Id = "1" });
        return paragraph;
    }

    private static SectionProperties Page() => new(
        new PageSize { Width = 11906U, Height = 16838U },
        new PageMargin { Top = 1134, Right = 850U, Bottom = 1134, Left = 1701U, Header = 708U, Footer = 708U, Gutter = 0U });

    // Закладка в теле или в ячейке таблицы заданной ширины.
    private static (WordprocessingDocument Document, DocxResult Result) Print(
        string html, IReadOnlyDictionary<Guid, ImageInfo>? images = null, int? cellWidth = null, string before = "")
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            OpenXmlElement content = cellWidth is { } width
                ? new Table(new TableProperties(new TableLayout { Type = TableLayoutValues.Fixed }), new TableGrid(new GridColumn { Width = width.ToString() }),
                    new TableRow(new TableCell(
                        new TableCellProperties(new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa }), Bookmarked())))
                : Bookmarked(before);
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body(content, new Paragraph(), Page()));
            main.Document.Save();
        }
        var result = DocxGenerator.Generate(stream.ToArray(), Mapping, Context(html, images ?? Images()));
        var docx = WordprocessingDocument.Open(new MemoryStream(result.Content), false);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(docx).Select(e => $"{e.Path?.XPath}: {e.Description}"));
        return (docx, result);
    }

    private static List<DW.Inline> Drawings(WordprocessingDocument docx) =>
        docx.MainDocumentPart!.Document!.Body!.Descendants<DW.Inline>().ToList();

    [Fact]
    public void Image_is_printed_at_the_size_of_the_markup_with_alt_text_and_a_picture_part()
    {
        var (docx, result) = Print($"<p>Схема</p><p>{Img(Scheme, "width=\"120\" height=\"60\" alt=\"Схема цепи\"")}</p>");

        var picture = Assert.Single(Drawings(docx));
        Assert.Equal((120 * Emu, 60 * Emu), (picture.Extent!.Cx!.Value, picture.Extent.Cy!.Value));
        Assert.Equal("Схема цепи", picture.GetFirstChild<DW.DocProperties>()!.Description!.Value);
        var part = Assert.Single(docx.MainDocumentPart!.ImageParts);
        Assert.Equal("image/png", part.ContentType);
        using var copy = new MemoryStream();
        using (var stored = part.GetStream()) stored.CopyTo(copy);
        Assert.Equal(Info(TestImages.Png(200, 100, seed: 1)).Content, copy.ToArray());
        Assert.Empty(result.Report.Unfilled);
        Assert.Empty(result.Report.Warnings);
        // Рисунок — в своём абзаце: подпись и рисунок не слиплись.
        Assert.Equal(["Схема", ""], docx.MainDocumentPart.Document!.Body!.Elements<Paragraph>().Take(2).Select(p => p.InnerText));
    }

    [Fact]
    public void Image_without_size_is_printed_at_its_own_size_and_a_single_side_keeps_proportions()
    {
        var (docx, _) = Print($"<p>{Img(Scheme)}{Img(Scheme, "width=\"100\"")}{Img(Scheme, "height=\"50\"")}</p>");

        Assert.Equal([(200 * Emu, 100 * Emu), (100 * Emu, 50 * Emu), (100 * Emu, 50 * Emu)],
            Drawings(docx).Select(d => (d.Extent!.Cx!.Value, d.Extent.Cy!.Value)));
    }

    [Fact]
    public void Image_wider_than_the_text_is_scaled_to_the_text_width_keeping_proportions()
    {
        var (docx, _) = Print($"<p>{Img(Scheme, "width=\"2000\" height=\"1000\"")}</p>");

        var extent = Assert.Single(Drawings(docx)).Extent!;
        Assert.InRange(extent.Cx!.Value, TextWidthDxa * DxaEmu - 1, TextWidthDxa * DxaEmu);
        Assert.InRange(extent.Cy!.Value * 2.0 / extent.Cx.Value, 0.999, 1.001);
    }

    [Fact]
    public void Image_in_a_table_cell_is_not_wider_than_the_cell()
    {
        const int cell = 3000;
        var (docx, _) = Print($"<p>{Img(Scheme, "width=\"900\" height=\"450\"")}</p>", cellWidth: cell);

        var extent = Assert.Single(Drawings(docx)).Extent!;
        Assert.True(extent.Cx!.Value < cell * DxaEmu);
        Assert.InRange(extent.Cy!.Value * 2.0 / extent.Cx.Value, 0.999, 1.001);
    }

    [Fact]
    public void Very_tall_image_is_not_taller_than_the_page()
    {
        var (docx, _) = Print($"<p>{Img(Scheme, "width=\"400\" height=\"6000\"")}</p>");

        var extent = Assert.Single(Drawings(docx)).Extent!;
        Assert.True(extent.Cy!.Value <= 8_640_000);
        Assert.InRange(extent.Cx!.Value * 15.0 / extent.Cy.Value, 0.999, 1.001 + 1e-3);
    }

    [Fact]
    public void Image_in_the_line_stays_with_the_text_and_the_same_file_is_one_part()
    {
        var (docx, _) = Print($"<p>до {Img(Scheme, "width=\"20\"")} после {Img(Scheme, "width=\"30\"")}, {Img(Graph, "width=\"40\"")}</p>");

        var paragraph = Assert.Single(docx.MainDocumentPart!.Document!.Body!.Elements<Paragraph>(), p => p.InnerText.Contains("до"));
        Assert.Equal(3, paragraph.Descendants<DW.Inline>().Count());
        Assert.StartsWith("до ", paragraph.InnerText);
        Assert.EndsWith(", ", paragraph.InnerText.TrimEnd() + " ");
        Assert.Equal(2, docx.MainDocumentPart.ImageParts.Count());
        Assert.Equal(["image/png", "image/jpeg"], docx.MainDocumentPart.ImageParts.Select(p => p.ContentType).OrderBy(t => t == "image/png" ? 0 : 1));
        // Номера рисунков в документе не повторяются.
        var ids = Drawings(docx).Select(d => d.GetFirstChild<DW.DocProperties>()!.Id!.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Image_alone_is_not_an_empty_field()
    {
        var (_, result) = Print(Img(Scheme, "width=\"50\""));

        Assert.Empty(result.Report.Unfilled);
        Assert.False(new PrintValue(RichTextBlocks.Parse(Img(Scheme), () => 1)).IsEmpty);
        Assert.True(new PrintValue(RichTextBlocks.Parse("<p> </p>", () => 1)).IsEmpty);
    }

    [Fact]
    public void Image_in_a_table_is_a_cell_paragraph_with_the_picture()
    {
        var (docx, _) = Print($"<table><tbody><tr><td>слева</td><td>{Img(Scheme, "width=\"100\"")}</td></tr></tbody></table>");

        var table = docx.MainDocumentPart!.Document!.Body!.Descendants<Table>().Single();
        Assert.Empty(table.Elements<TableRow>().Single().Elements<TableCell>().First().Descendants<DW.Inline>());
        Assert.Single(table.Elements<TableRow>().Single().Elements<TableCell>().Last().Descendants<DW.Inline>());
    }

    [Fact]
    public void Image_in_a_paragraph_with_other_text_is_not_lost_silently()
    {
        // Закладка стоит в абзаце с текстом («Схема: …»): значение печатается строкой, рисунку в ней места нет — отчёт это называет.
        var (docx, result) = Print($"<p>{Img(Scheme, "width=\"20\"")}</p>", before: "Схема: ");

        Assert.Empty(Drawings(docx));
        Assert.Contains(result.Report.Warnings, w => w.Contains("рисунок не напечатан"));
    }

    [Fact]
    public void Missing_image_is_skipped_with_a_warning_and_the_text_stays()
    {
        var (docx, result) = Print($"<p>Текст {Img(Guid.NewGuid())} остался</p>", images: new Dictionary<Guid, ImageInfo>());

        Assert.Empty(Drawings(docx));
        Assert.Contains(result.Report.Warnings, w => w.Contains("не найден"));
        Assert.Contains("Текст", docx.MainDocumentPart!.Document!.Body!.InnerText);
    }
}
