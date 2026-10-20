using RpdFos.Core.Data.Entities;
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
    public void Occurrence_overrides_name_real_template_occurrences(string type, string template)
    {
        var names = DocxBookmarks.Read(DocxBookmarksTests.Template(template)).SelectMany(g => g.Names).ToHashSet();
        var overrides = Registry.Get(type).PrintMapping.Values.SelectMany(t => t.Occurrences?.Keys ?? []).ToList();

        Assert.NotEmpty(overrides);
        Assert.All(overrides, o => Assert.Contains(o, names));
    }

    // Формы и цели вхождений — по контексту шаблонов (решения по сверке B3).
    [Theory]
    [InlineData(DocumentTypeRegistry.Rpd, "AutofillPartType", "AutofillPartType2", WordForms.Dat, null)]
    [InlineData(DocumentTypeRegistry.Rpd, "AutofillEducationLevel", "AutofillEducationLevel2", null, "reference.direction.qualification")]
    [InlineData(DocumentTypeRegistry.Fos, "AutofillEducationForm", "AutofillEducationForm1", WordForms.Gen, null)]
    [InlineData(DocumentTypeRegistry.Fos, "AutofillEducationLvl", "AutofillEducationLvl1", WordForms.Gen, null)]
    [InlineData(DocumentTypeRegistry.Fos, "AutofillMonitoringU", "AutofillMonitoringU1", WordForms.Dat, null)]
    [InlineData(DocumentTypeRegistry.Fos, "AutofillMonitoring", "AutofillMonitoring3", WordForms.Gen, null)]
    public void Occurrences_follow_template_context(string type, string bookmark, string occurrence, string? form, string? target)
    {
        var o = Registry.Get(type).PrintMapping[bookmark].Occurrences![occurrence];

        Assert.Equal((form, target), (o.Form, o.Target));
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
    public void Role_lookups_use_directory_signatory_codes()
    {
        // Один набор кодов категорий: lookup.role:<код> ищет по users.signatory_role.
        var lookups = SignatureResolvers.All.Keys.Where(k => k.StartsWith("lookup.role:")).Select(k => k["lookup.role:".Length..]);

        Assert.All(lookups, code => Assert.Contains(code, SignatoryRoles.All));
    }

    [Fact]
    public void Every_signatory_category_has_russian_title()
    {
        // В интерфейсе — название, код только в подсказке.
        Assert.All(SignatoryRoles.All, code => Assert.NotEqual(code, SignatoryRoles.Title(code)));
        Assert.Equal("Проректор по образовательной деятельности", SignatoryRoles.Title(SignatoryRoles.ViceRectorEdu));
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
            ["AutofillF"] = new("picture", "logo"),
            ["AutofillG"] = new(BookmarkTargetKinds.Source, "document.year", Occurrences: new Dictionary<string, BookmarkOccurrence>
            {
                ["AutofillG1"] = new(Form: WordForms.Gen),
                ["AutofillH2"] = new(Form: WordForms.Gen),
                ["AutofillG3"] = new(Form: "vocative"),
                ["AutofillG4"] = new(Target: "/title_page/no_such_field", Kind: BookmarkTargetKinds.Field)
            })
        };
        var signatures = Registry.Get(DocumentTypeRegistry.Rpd).Signatures;

        var errors = PrintMappingValidator.Validate(Registry, DocumentTypeRegistry.Rpd, mapping, signatures,
            [.. mapping.Keys, "AutofillUnmapped"]);

        Assert.Equal(10, errors.Count);
        Assert.Contains("Закладка AutofillUnmapped не сопоставлена", errors);
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillA:") && e.Contains("нет в схеме"));
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillB:") && e.Contains("нет в листе согласования"));
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillG, вхождение AutofillH2:") && e.Contains("другой закладке"));
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillG, вхождение AutofillG3:") && e.Contains("форма"));
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillG, вхождение AutofillG4:") && e.Contains("нет в схеме"));
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
