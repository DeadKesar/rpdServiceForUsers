using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>Правила переходов статусов проверяются без HTTP и без базы.</summary>
public class TemplateTransitionsTests
{
    [Theory]
    [InlineData(TemplateStatuses.Empty, true)]
    [InlineData(TemplateStatuses.Draft, true)]
    [InlineData(TemplateStatuses.SavedDraft, true)]
    [InlineData(TemplateStatuses.Published, false)]
    [InlineData(TemplateStatuses.Archived, false)]
    [InlineData(TemplateStatuses.Deleted, false)]
    public void CanPublish(string status, bool expected) =>
        Assert.Equal(expected, TemplateTransitions.CanPublish(status));
}
