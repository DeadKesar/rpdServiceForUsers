using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;
using Xunit;
using S = RpdFos.Core.Data.Entities.TemplateStatuses;

namespace RpdFos.Api.Tests;

/// <summary>Жизненный цикл шаблона (B5): правила переходов и методы сущности — без HTTP и без базы.</summary>
public class TemplateTransitionsTests
{
    private static readonly DocumentTypeRegistry Registry = new();

    [Theory]
    [InlineData(S.Empty, false)]
    [InlineData(S.Draft, true)]
    [InlineData(S.SavedDraft, true)]
    [InlineData(S.Published, false)]
    [InlineData(S.Archived, false)]
    [InlineData(S.Deleted, false)]
    public void CanPublish(string status, bool expected) =>
        // Публикация из «Пустого» закрыта проверкой перед публикацией (правило этапа 0 обновлено).
        Assert.Equal(expected, TemplateTransitions.CanPublish(status));

    [Theory]
    [InlineData(S.Empty, true, false, false, true, false)]
    [InlineData(S.Draft, true, true, false, true, false)]
    [InlineData(S.SavedDraft, true, false, false, true, false)]
    [InlineData(S.Published, false, false, true, false, true)]
    [InlineData(S.Archived, false, false, false, false, false)]
    [InlineData(S.Deleted, false, false, false, false, false)]
    public void Transitions_by_status(string status, bool attach, bool save, bool archive, bool delete, bool revision)
    {
        Assert.Equal(attach, TemplateTransitions.CanAttachPrintTemplate(status));
        Assert.Equal(save, TemplateTransitions.CanSave(status));
        Assert.Equal(archive, TemplateTransitions.CanArchive(status));
        Assert.Equal(delete, TemplateTransitions.CanDelete(status));
        Assert.Equal(revision, TemplateTransitions.CanCreateRevision(status));
    }

    [Fact]
    public void Created_template_is_empty_with_snapshot_of_type()
    {
        var type = Registry.Get(DocumentTypeRegistry.Fos);
        var plan = Guid.NewGuid();
        var author = Guid.NewGuid();

        var t = DocumentTemplate.Create(type, "ФОС 09.04.04", plan, author);

        Assert.Equal((S.Empty, 1, t.Id), (t.Status, t.Revision, t.LineageId));
        Assert.Equal((DocumentTypeRegistry.Fos, plan, author), (t.Type, t.CurriculumPlanId, t.CreatedBy));
        Assert.Equal((type.SchemaJson, type.SignaturesJson), (t.Schema, t.SignaturesConfig));
        Assert.Null(t.PrintTemplateFileId);
    }

    [Fact]
    public void Lifecycle_goes_through_draft_saved_published_archived()
    {
        var t = NewTemplate();
        var file = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        t.AttachPrintTemplate(file, "{}");
        Assert.Equal((S.Draft, file, "{}"), (t.Status, t.PrintTemplateFileId, t.PrintMapping));

        t.Save();
        Assert.Equal(S.SavedDraft, t.Status);

        // Повторная загрузка оформления возвращает к черновику.
        t.AttachPrintTemplate(file, "{}");
        Assert.Equal(S.Draft, t.Status);

        t.Publish(now);
        Assert.Equal((S.Published, (DateTimeOffset?)now), (t.Status, t.PublishedAt));

        t.Archive();
        Assert.Equal(S.Archived, t.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_sets_status_and_flag_together(bool withPrintTemplate)
    {
        var t = NewTemplate();
        if (withPrintTemplate) t.AttachPrintTemplate(Guid.NewGuid(), "{}");
        var now = DateTimeOffset.UtcNow;

        t.Delete(now);

        Assert.Equal((S.Deleted, true, (DateTimeOffset?)now), (t.Status, t.IsDeleted, t.DeletedAt));
    }

    [Fact]
    public void Revision_copies_published_template_into_new_draft_of_same_lineage()
    {
        var t = NewTemplate();
        t.AttachPrintTemplate(Guid.NewGuid(), """{"AutofillYear":{"kind":"source","target":"document.year"}}""");
        t.Publish(DateTimeOffset.UtcNow);
        var author = Guid.NewGuid();

        var r = t.CreateRevision(2, author);

        Assert.NotEqual(t.Id, r.Id);
        Assert.Equal((t.LineageId, 2, S.Draft, author), (r.LineageId, r.Revision, r.Status, r.CreatedBy));
        Assert.Equal((t.Type, t.Name, t.CurriculumPlanId), (r.Type, r.Name, r.CurriculumPlanId));
        Assert.Equal((t.Schema, t.SignaturesConfig, t.PrintMapping, t.PrintTemplateFileId),
            (r.Schema, r.SignaturesConfig, r.PrintMapping, r.PrintTemplateFileId));
        Assert.Null(r.PublishedAt);
        // Прежняя редакция остаётся опубликованной до публикации новой.
        Assert.Equal(S.Published, t.Status);
    }

    [Fact]
    public void Forbidden_transition_throws_with_status_title()
    {
        var t = NewTemplate();

        var e = Assert.Throws<InvalidOperationException>(() => t.Publish(DateTimeOffset.UtcNow));

        Assert.Equal("Шаблон в статусе «Пустой» нельзя опубликовать.", e.Message);
        Assert.Throws<InvalidOperationException>(t.Save);
        Assert.Throws<InvalidOperationException>(t.Archive);
        Assert.Throws<InvalidOperationException>(() => t.CreateRevision(2, Guid.NewGuid()));
    }

    private static DocumentTemplate NewTemplate() =>
        DocumentTemplate.Create(Registry.Get(DocumentTypeRegistry.Rpd), "РПД", Guid.NewGuid(), Guid.NewGuid());
}
