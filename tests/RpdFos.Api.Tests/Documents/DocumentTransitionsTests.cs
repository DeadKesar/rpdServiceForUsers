using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using Xunit;
using S = RpdFos.Core.Documents.DocumentStatuses;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Жизненный цикл проверки документа (этап 5, B1): правила переходов и методы сущности — без HTTP и без базы.</summary>
public class DocumentTransitionsTests
{
    [Theory]
    [InlineData(S.Draft, true, false, true, false, false)]
    [InlineData(S.SavedDraft, true, false, true, false, false)]
    [InlineData(S.UnderReview, false, true, false, true, false)]
    [InlineData(S.Returned, true, false, true, false, true)]
    [InlineData(S.Accepted, false, false, false, false, false)]
    [InlineData(S.Approval, false, false, false, false, false)]
    [InlineData(S.Approved, false, false, false, false, false)]
    [InlineData(S.Archived, false, false, false, false, false)]
    [InlineData(S.Deleted, false, false, false, false, false)]
    public void Transitions_by_status(string status, bool authorEdits, bool reviewerEdits, bool submit, bool review, bool resolve)
    {
        // На проверке и после приёма документ закрыт для автора; методист правит только на проверке.
        Assert.Equal(authorEdits, DocumentTransitions.CanEdit(status, DocumentRole.Author));
        Assert.Equal(authorEdits, DocumentTransitions.CanSave(status, DocumentRole.Author));
        Assert.Equal(reviewerEdits, DocumentTransitions.CanEdit(status, DocumentRole.Reviewer));
        Assert.Equal(submit, DocumentTransitions.CanSubmit(status));
        Assert.Equal(review, DocumentTransitions.CanReview(status));
        Assert.Equal(resolve, DocumentTransitions.CanResolveComments(status));
    }

    [Theory]
    [InlineData(S.Draft, S.SavedDraft)]
    [InlineData(S.SavedDraft, S.SavedDraft)]
    [InlineData(S.Returned, S.Returned)]
    [InlineData(S.UnderReview, S.UnderReview)]
    public void Save_changes_status_only_from_draft(string status, string expected)
    {
        // На доработке и на проверке «Сохранить» — только снимок версии: статус держит смысл «на доработке».
        var document = new Document { Status = status };

        document.Save();

        Assert.Equal(expected, document.Status);
    }

    [Theory]
    [InlineData(S.Draft, S.Draft)]
    [InlineData(S.SavedDraft, S.Draft)]
    [InlineData(S.Returned, S.Returned)]
    [InlineData(S.UnderReview, S.UnderReview)]
    public void Edit_keeps_review_statuses(string status, string expected)
    {
        var document = new Document { Status = status };

        document.Edit("{}");

        Assert.Equal(expected, document.Status);
    }

    [Fact]
    public void Review_cycle_moves_through_statuses()
    {
        var now = DateTimeOffset.UtcNow;
        var document = new Document { Status = S.SavedDraft };

        Assert.Equal(S.SavedDraft, document.Submit(now));
        Assert.Equal((S.UnderReview, now), (document.Status, document.SubmittedAt));
        Assert.Equal(S.UnderReview, document.ReturnForRework());
        Assert.Equal(S.Returned, document.Status);

        // Повторная отправка — с доработки, той же проверкой.
        Assert.Equal(S.Returned, document.Submit(now.AddDays(1)));
        Assert.Equal(S.UnderReview, document.Accept());
        Assert.Equal(S.Accepted, document.Status);
    }

    [Theory]
    [InlineData(S.Accepted)]
    [InlineData(S.UnderReview)]
    public void Closed_actions_throw(string status)
    {
        var document = new Document { Status = status };

        Assert.Throws<InvalidOperationException>(() => document.Submit(DateTimeOffset.UtcNow));
        if (status == S.Accepted)
        {
            Assert.Throws<InvalidOperationException>(() => document.Edit("{}"));
            Assert.Throws<InvalidOperationException>(document.ReturnForRework);
            Assert.Throws<InvalidOperationException>(document.Accept);
        }
    }

    [Fact]
    public void Comment_is_resolved_once()
    {
        var comment = new DocumentComment();
        var by = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        Assert.True(comment.Resolve(by, at));
        Assert.False(comment.Resolve(Guid.NewGuid(), at.AddHours(1)));
        Assert.Equal((true, by, at), (comment.IsResolved, comment.ResolvedBy, comment.ResolvedAt));
    }
}
