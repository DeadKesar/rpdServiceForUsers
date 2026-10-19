using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>Чтение закладок DOCX-шаблонов оформления (B3) — на реальных шаблонах DWP и FOS.</summary>
public class DocxBookmarksTests
{
    public static byte[] Template(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public const string Rpd = "DWP_TemplateBookmarks.docx", Fos = "FOS_TemplateBookmarks.docx";

    [Theory]
    [InlineData(Rpd, 52, 35)]
    [InlineData(Fos, 46, 24)]
    public void Real_templates_match_bookmark_catalog(string file, int bookmarks, int baseNames)
    {
        // Каталог закладок (docs/design/ВКР_2_Карта_закладок_DOCX.md): 52 в РПД и 46 в ФОС.
        var groups = DocxBookmarks.Read(Template(file));

        Assert.Equal(bookmarks, groups.Sum(g => g.Count));
        Assert.Equal(baseNames, groups.Count);
        Assert.All(groups, g => Assert.StartsWith(DocxBookmarks.Prefix, g.BaseName));
    }

    [Fact]
    public void Fan_out_counts_follow_catalog()
    {
        var rpd = DocxBookmarks.Read(Template(Rpd)).ToDictionary(g => g.BaseName, g => g.Count);
        var fos = DocxBookmarks.Read(Template(Fos)).ToDictionary(g => g.BaseName, g => g.Count);

        Assert.Equal((7, 4, 4, 2), (rpd["AutofillDiscipline"], rpd["AutofillWayCode"], rpd["AutofillWayName"], rpd["AutofillWaySection"]));
        Assert.Equal((7, 3, 6, 6), (fos["AutofillDiscipline"], fos["AutofillWayName"], fos["AutofillYear"], fos["AutofillDepartment"]));
        // Несогласованность имён действующих шаблонов (каталог, замечание 2): Kurs/Curs, EducationLevel/EducationLvl.
        Assert.True(rpd.ContainsKey("AutofillKurs") && fos.ContainsKey("AutofillCurs"));
        Assert.True(fos.ContainsKey("AutofillEducationLevel") && fos.ContainsKey("AutofillEducationLvl"));
        // MonitoringU1 и Monitoring2–3 — разные базовые имена.
        Assert.Equal((1, 2), (fos["AutofillMonitoringU"], fos["AutofillMonitoring"]));
    }

    [Fact]
    public void Numeric_suffix_of_any_length_is_dropped()
    {
        // У предшественника отбрасывался один символ: «AutofillDiscipline12» превращался в «AutofillDiscipline1».
        var docx = Docx("AutofillDiscipline3", "AutofillDiscipline12", "AutofillDiscipline107", "AutofillYear", "Other1");

        var groups = DocxBookmarks.Read(docx);

        Assert.Equal([new BookmarkGroup("AutofillDiscipline", 3), new BookmarkGroup("AutofillYear", 1)], groups);
    }

    private static byte[] Docx(params string[] bookmarks)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            var id = 0;
            foreach (var name in bookmarks)
            {
                var p = new Paragraph();
                p.Append(new BookmarkStart { Name = name, Id = (++id).ToString() }, new Run(new Text(name)),
                    new BookmarkEnd { Id = id.ToString() });
                body.Append(p);
            }
            document.AddMainDocumentPart().Document = new Document(body);
        }
        return stream.ToArray();
    }
}
