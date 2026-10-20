using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;
using Xunit;

namespace RpdFos.Api.Tests.DocumentTypes;

/// <summary>Проверка шаблона перед публикацией (FR-TPL-11, FR-TPL-13) — на реальных шаблонах оформления.</summary>
public class TemplateCheckTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    [Theory]
    [InlineData(DocumentTypeRegistry.Rpd, DocxBookmarksTests.Rpd)]
    [InlineData(DocumentTypeRegistry.Fos, DocxBookmarksTests.Fos)]
    public void Template_with_plan_and_real_print_template_passes(string type, string file)
    {
        var docx = DocxBookmarksTests.Template(file);
        var t = WithPrintTemplate(type, docx);

        var inspection = TemplateCheck.Inspect(t, docx, Registry);

        Assert.Empty(inspection.Errors);
        Assert.True(inspection.CanPublish);
        Assert.Equal(inspection.Bookmarks.Select(b => b.BaseName).Order(), inspection.Mapping.Keys.Order());
    }

    [Fact]
    public void Template_without_plan_and_print_template_lists_both_reasons()
    {
        // Шаблон этапов 0–1: без учебного плана и без оформления.
        var t = new DocumentTemplate { SignaturesConfig = Registry.Get(DocumentTypeRegistry.Rpd).SignaturesJson };

        var errors = TemplateCheck.Inspect(t, null, Registry).Errors;

        Assert.Equal(["Не привязан учебный план.", "Не загружен DOCX-шаблон оформления."], errors);
    }

    [Fact]
    public void Print_template_of_other_type_has_unmapped_bookmarks()
    {
        // В шаблон РПД загружено оформление ФОС: закладок ФОС нет в сопоставлении РПД.
        var fos = DocxBookmarksTests.Template(DocxBookmarksTests.Fos);
        var t = WithPrintTemplate(DocumentTypeRegistry.Rpd, fos);

        var errors = TemplateCheck.Inspect(t, fos, Registry).Errors;

        Assert.Contains("Закладка AutofillCurs не сопоставлена", errors);
        Assert.Contains("Закладка AutofillProOD не сопоставлена", errors);
        Assert.DoesNotContain(errors, e => e.Contains("AutofillDiscipline"));
    }

    [Fact]
    public void Missing_target_and_unknown_resolver_are_reported()
    {
        var docx = DocxBookmarksTests.Template(DocxBookmarksTests.Rpd);
        var t = WithPrintTemplate(DocumentTypeRegistry.Rpd, docx);
        t.PrintMapping = t.PrintMapping!.Replace("/title_page/discipline_name", "/title_page/no_such_field");
        t.SignaturesConfig = t.SignaturesConfig.Replace("lookup.role:umu_head", "lookup.role:dean");

        var errors = TemplateCheck.Inspect(t, docx, Registry).Errors;

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Закладка AutofillDiscipline:") && e.Contains("нет в схеме"));
        Assert.Contains(errors, e => e.StartsWith("Место umu_head:") && e.Contains("lookup.role:dean"));
    }

    [Fact]
    public void Default_mapping_keeps_only_bookmarks_of_uploaded_template()
    {
        var bookmarks = DocxBookmarks.Read(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd)).Take(3).ToList();

        var mapping = PrintMappingJson.ReadMapping(TemplateCheck.DefaultMapping(Registry.Get(DocumentTypeRegistry.Rpd), bookmarks));

        Assert.Equal(bookmarks.Select(b => b.BaseName).Order(), mapping.Keys.Order());
        // Переопределения вхождений и пояснения переходят в сопоставление шаблона.
        var full = PrintMappingJson.ReadMapping(TemplateCheck.DefaultMapping(Registry.Get(DocumentTypeRegistry.Rpd),
            DocxBookmarks.Read(DocxBookmarksTests.Template(DocxBookmarksTests.Rpd))));
        Assert.Equal(WordForms.Dat, full["AutofillPartType"].Occurrences!["AutofillPartType2"].Form);
        Assert.NotNull(full["AutofillKurs"].Note);
    }

    private static DocumentTemplate WithPrintTemplate(string type, byte[] docx)
    {
        var definition = Registry.Get(type);
        var t = DocumentTemplate.Create(definition, "Шаблон", Guid.NewGuid(), Guid.NewGuid());
        t.AttachPrintTemplate(Guid.NewGuid(), TemplateCheck.DefaultMapping(definition, DocxBookmarks.Read(docx)));
        return t;
    }
}
