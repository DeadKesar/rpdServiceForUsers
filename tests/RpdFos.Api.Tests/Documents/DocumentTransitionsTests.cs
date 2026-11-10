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
    public void Approval_cycle_moves_through_statuses()
    {
        var now = DateTimeOffset.UtcNow;
        var document = new Document { Status = S.UnderReview };

        document.Accept();
        Assert.Equal(S.Accepted, document.StartApproval());
        // Возврат с согласования — методисту в «На проверке», а не автору (UC-09).
        Assert.Equal(S.Approval, document.ReturnFromApproval());
        Assert.Equal(S.UnderReview, document.Status);

        document.Accept();
        document.StartApproval();
        Assert.Equal(S.Approval, document.Approve(now));
        Assert.Equal((S.Approved, now), (document.Status, document.ApprovedAt));
    }

    [Theory]
    [InlineData(S.Draft, false, false, false, false)]
    [InlineData(S.UnderReview, false, false, false, true)]
    [InlineData(S.Accepted, true, false, true, true)]
    [InlineData(S.Approval, false, true, true, false)]
    [InlineData(S.Approved, false, false, false, false)]
    public void Approval_actions_by_status(string status, bool start, bool sign, bool assign, bool correctPlan)
    {
        // В «Принят» ждут назначения и исправляют поля плана; на согласовании исправлений нет; согласованный закрыт.
        Assert.Equal(start, DocumentTransitions.CanStartApproval(status));
        Assert.Equal(sign, DocumentTransitions.CanSign(status));
        Assert.Equal(assign, DocumentTransitions.CanAssignSignatory(status));
        Assert.Equal(correctPlan, DocumentTransitions.CanCorrectPlanFields(status));

        var document = new Document { Status = status };
        if (!start) Assert.Throws<InvalidOperationException>(() => document.StartApproval());
        if (!sign)
        {
            Assert.Throws<InvalidOperationException>(() => document.Approve(DateTimeOffset.UtcNow));
            Assert.Throws<InvalidOperationException>(() => document.ReturnFromApproval());
        }
    }

    [Theory]
    [InlineData(S.Draft, false, false)]
    [InlineData(S.UnderReview, true, false)]
    [InlineData(S.Accepted, true, false)]
    [InlineData(S.Approval, true, true)]
    [InlineData(S.Approved, true, true)]
    public void Printing_by_status_and_role(string status, bool reviewer, bool signatory)
    {
        // Автор печатает всегда; методист — с проверки; согласующий — на согласовании и после; остальные — нет.
        Assert.True(DocumentTransitions.CanPrint(status, isAuthor: true, isReviewer: false, isSignatory: false));
        Assert.Equal(reviewer, DocumentTransitions.CanPrint(status, isAuthor: false, isReviewer: true, isSignatory: false));
        Assert.Equal(signatory, DocumentTransitions.CanPrint(status, isAuthor: false, isReviewer: false, isSignatory: true));
        Assert.False(DocumentTransitions.CanPrint(status, isAuthor: false, isReviewer: false, isSignatory: false));
    }

    [Fact]
    public void Signature_is_marked_once_and_reassigned_only_while_pending()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var row = new DocumentSignature { Role = SignatoryRoles.UmuHead };
        row.Reset("lookup.role:umu_head", first, now);

        row.Assign(second);
        row.Approve(now);
        Assert.Equal((second, SignatureStatuses.Approved, now), (row.ResolvedUserId, row.Status, row.ApprovedAt));
        Assert.Throws<InvalidOperationException>(() => row.Assign(first));
        Assert.Throws<InvalidOperationException>(() => row.Reject("Поздно"));

        // Новый круг: строка снова ждёт отметки, прежняя отметка снята.
        row.Reset("lookup.role:umu_head", first, now.AddDays(1));
        Assert.Equal((first, SignatureStatuses.Pending, null, null), (row.ResolvedUserId, row.Status, row.ApprovedAt, row.Comment));
        row.Reject("Нет литературы");
        Assert.Equal((SignatureStatuses.Rejected, "Нет литературы"), (row.Status, row.Comment));
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
