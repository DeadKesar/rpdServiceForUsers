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

    [Theory]
    [InlineData(S.UnderReview, true, false)]
    [InlineData(S.Approval, false, true)]
    [InlineData(S.Accepted, false, false)]
    [InlineData(S.Returned, false, false)]
    [InlineData(S.Approved, false, false)]
    public void Comments_are_left_by_methodist_on_review_and_by_approver_awaiting_mark(string status, bool reviewer, bool approver)
    {
        // Этап 7, A2: согласующий — на согласовании и только пока его строка ждёт отметки.
        Assert.Equal(reviewer, DocumentTransitions.CanComment(status, isReviewer: true, awaitingMark: false));
        Assert.Equal(approver, DocumentTransitions.CanComment(status, isReviewer: false, awaitingMark: true));
        Assert.False(DocumentTransitions.CanComment(status, isReviewer: false, awaitingMark: false));
    }

    [Fact]
    public void Comments_are_resolved_by_author_on_rework_methodist_on_review_and_approver_awaiting_mark()
    {
        static bool Resolve(string status, bool author = false, bool reviewer = false, bool awaiting = false, bool own = false,
            bool fromApproval = false) =>
            DocumentTransitions.CanResolveComment(status, author, reviewer, awaiting, own, fromApproval);

        // Автор на доработке — любое, в том числе согласующего; вне доработки — никакое.
        Assert.True(Resolve(S.Returned, author: true, fromApproval: true));
        Assert.False(Resolve(S.Approval, author: true, own: true));
        // Методист на проверке — своё и оставленное на согласовании, но не другого методиста.
        Assert.True(Resolve(S.UnderReview, reviewer: true, own: true));
        Assert.True(Resolve(S.UnderReview, reviewer: true, fromApproval: true));
        Assert.False(Resolve(S.UnderReview, reviewer: true));
        Assert.False(Resolve(S.Returned, reviewer: true, fromApproval: true));
        // Согласующий — своё, пока ждёт отметки; чужое и после возврата — нет.
        Assert.True(Resolve(S.Approval, awaiting: true, own: true, fromApproval: true));
        Assert.False(Resolve(S.Approval, awaiting: true, fromApproval: true));
        Assert.False(Resolve(S.Approval, own: true, fromApproval: true));
        Assert.False(Resolve(S.UnderReview, awaiting: true, own: true, fromApproval: true));
    }

    [Theory]
    [InlineData(S.Draft, false)]
    [InlineData(S.SavedDraft, false)]
    [InlineData(S.UnderReview, false)]
    [InlineData(S.Returned, false)]
    [InlineData(S.Accepted, false)]
    [InlineData(S.Approval, false)]
    [InlineData(S.Approved, true)]
    [InlineData(S.Archived, false)]
    [InlineData(S.Deleted, false)]
    public void Only_an_approved_document_is_returned_for_correction(string status, bool allowed)
    {
        // Этап 7, A8, решение 2: согласованный документ на исправление возвращает методист, и только согласованный.
        Assert.Equal(allowed, DocumentTransitions.CanReturnForCorrection(status));

        var document = new Document { Status = status, ApprovedAt = DateTimeOffset.UtcNow };
        if (!allowed)
        {
            Assert.Throws<InvalidOperationException>(document.ReturnForCorrection);
            return;
        }
        Assert.Equal(S.Approved, document.ReturnForCorrection());
        // Время согласования сбрасывается; снимок согласованной версии — в document_versions, документ его не хранит.
        Assert.Equal((S.UnderReview, null), (document.Status, document.ApprovedAt));
    }

    [Theory]
    [InlineData(S.Draft, "saved_draft,returned,under_review")]
    [InlineData(S.SavedDraft, "returned,under_review")]
    [InlineData(S.UnderReview, "saved_draft,returned")]
    [InlineData(S.Returned, "saved_draft,under_review")]
    [InlineData(S.Accepted, "saved_draft,returned,under_review")]
    [InlineData(S.Approval, "saved_draft,returned,under_review")]
    [InlineData(S.Approved, "saved_draft,returned,under_review")]
    [InlineData(S.Archived, "saved_draft,returned,under_review")]
    [InlineData(S.Deleted, "")]
    public void Forced_status_goes_only_backwards_from_any_status_but_deleted(string from, string expected)
    {
        // Этап 7, A8; FR-ADM-04: из любого статуса, кроме «Удалённый», в «Сохранённый черновик», «На доработке» или «На проверке».
        Assert.Equal(expected, string.Join(',', DocumentTransitions.ForceTargetsFrom(from)));

        // В «Черновик», «Принят», «На согласовании», «Согласован», «Архивный» и «Удалённый» принудительно не переводят.
        foreach (var forbidden in new[] { S.Draft, S.Accepted, S.Approval, S.Approved, S.Archived, S.Deleted })
            Assert.False(DocumentTransitions.CanForceStatus(from, forbidden), $"{from} → {forbidden}");
    }

    [Fact]
    public void Forced_status_clears_the_approval_time_and_dates_the_submission_only_when_it_is_missing()
    {
        var now = DateTimeOffset.UtcNow;
        var sent = now.AddDays(-5);

        // Согласие сбрасывается, дата отправки автором остаётся прежней.
        var approved = new Document { Status = S.Approved, ApprovedAt = now.AddDays(-1), SubmittedAt = sent };
        Assert.Equal(S.Approved, approved.ForceStatus(S.UnderReview, now));
        Assert.Equal((S.UnderReview, null, sent), (approved.Status, approved.ApprovedAt, approved.SubmittedAt));

        // Документ, ушедший на проверку не отправкой, получает дату — по ней он встаёт в очередь проверки.
        var draft = new Document { Status = S.Draft };
        Assert.Equal(S.Draft, draft.ForceStatus(S.UnderReview, now));
        Assert.Equal(now, draft.SubmittedAt);

        // В другие статусы даты отправки не ставят.
        var other = new Document { Status = S.Accepted };
        other.ForceStatus(S.Returned, now);
        Assert.Null(other.SubmittedAt);

        // Запрещённое — исключение: эндпоинт проверяет заранее, а сущность защищает от ошибки в коде.
        Assert.Throws<InvalidOperationException>(() => new Document { Status = S.Approved }.ForceStatus(S.Accepted, now));
        Assert.Throws<InvalidOperationException>(() => new Document { Status = S.UnderReview }.ForceStatus(S.UnderReview, now));
        Assert.Throws<InvalidOperationException>(() => new Document { Status = S.Deleted }.ForceStatus(S.Returned, now));
    }

    [Theory]
    [InlineData(S.Draft, true)]
    [InlineData(S.SavedDraft, true)]
    [InlineData(S.UnderReview, true)]
    [InlineData(S.Returned, true)]
    [InlineData(S.Accepted, true)]
    [InlineData(S.Approval, true)]
    [InlineData(S.Approved, false)]
    [InlineData(S.Archived, false)]
    [InlineData(S.Deleted, false)]
    public void Unfinished_document_is_not_approved_archived_or_deleted(string status, bool unfinished) =>
        // Уточнение планировщика, этап 7: на дисциплину и линию шаблона — не больше одного незавершённого документа.
        Assert.Equal(unfinished, DocumentTransitions.IsUnfinished(status));
}
