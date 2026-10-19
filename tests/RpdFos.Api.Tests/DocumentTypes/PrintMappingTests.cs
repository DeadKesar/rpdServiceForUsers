using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>Сопоставление закладок и лист согласования по умолчанию (B2) — по реальным шаблонам DWP и FOS.</summary>
public class PrintMappingTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    public static TheoryData<string, string> Templates => new()
    {
        { DocumentTypeRegistry.Rpd, DocxBookmarksTests.Rpd },
        { DocumentTypeRegistry.Fos, DocxBookmarksTests.Fos }
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_bookmark_of_real_template_is_mapped_and_every_target_exists(string type, string template)
    {
        var definition = Registry.Get(type);
        var bookmarks = DocxBookmarks.Read(DocxBookmarksTests.Template(template)).Select(b => b.BaseName).ToList();

        var errors = PrintMappingValidator.Validate(Registry, type, definition.PrintMapping, definition.Signatures, bookmarks);

        Assert.Empty(errors);
        // Сопоставление по умолчанию — ровно закладки действующего шаблона, без лишних.
        Assert.Equal(bookmarks.Order(), definition.PrintMapping.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Signatures_use_known_resolvers(string type, string _)
    {
        var signatures = Registry.Get(type).Signatures;

        Assert.Empty(PrintMappingValidator.ValidateSignatures(signatures));
        // Электронные места в прототипе — заведующий кафедрой дисциплины и начальник УМУ, остальные — бумажные.
        Assert.Equal(["department_head", "umu_head"], signatures.Where(s => s.Electronic).Select(s => s.Id).Order());
    }

    [Fact]
    public void Rpd_title_carries_vice_rector_approval_stamp()
    {
        var seat = Assert.Single(Registry.Get(DocumentTypeRegistry.Rpd).Signatures, s => s.Placement == SignaturePlacements.Title);

        Assert.Equal("vice_rector_edu", seat.Id);
    }

    [Fact]
    public void Validator_reports_unmapped_bookmarks_and_missing_targets()
    {
        // Проверка перед публикацией (B5) опирается на эти же ошибки.
        var mapping = new Dictionary<string, BookmarkTarget>
        {
            ["AutofillA"] = new(BookmarkTargetKinds.Field, "/title_page/no_such_field"),
            ["AutofillB"] = new(BookmarkTargetKinds.Signature, "rector", SignatureParts.Name),
            ["AutofillC"] = new(BookmarkTargetKinds.Table, "no_such_table"),
            ["AutofillD"] = new(BookmarkTargetKinds.Source, "parser.discipline.no_such_source"),
            ["AutofillE"] = new(BookmarkTargetKinds.Text, "по дисциплине «{parser.discipline.nam}»"),
            ["AutofillF"] = new("picture", "logo")
        };
        var signatures = Registry.Get(DocumentTypeRegistry.Rpd).Signatures;

        var errors = PrintMappingValidator.Validate(Registry, DocumentTypeRegistry.Rpd, mapping, signatures,
            [.. mapping.Keys, "AutofillUnmapped"]);

        Assert.Equal(7, errors.Count);
        Assert.Contains("Закладка AutofillUnmapped не сопоставлена", errors);
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillA:") && e.Contains("нет в схеме"));
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillB:") && e.Contains("нет в листе согласования"));
    }

    [Fact]
    public void Fos_fields_resolve_through_reference_to_rpd()
    {
        // Титул ФОС — $ref на схему РПД: поле находится по ссылке.
        Assert.True(Registry.FieldExists(DocumentTypeRegistry.Fos, "/title_page/discipline_name"));
        Assert.True(Registry.FieldExists(DocumentTypeRegistry.Fos, "/current_control/forms"));
        Assert.False(Registry.FieldExists(DocumentTypeRegistry.Fos, "/title_page/no_such_field"));
    }
}
