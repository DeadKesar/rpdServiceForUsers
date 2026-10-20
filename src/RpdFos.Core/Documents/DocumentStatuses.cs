namespace RpdFos.Core.Documents;

/// <summary>Девять состояний документа (FR-DOC-11, диаграмма состояний документа).</summary>
public static class DocumentStatuses
{
    public const string Draft = "draft", SavedDraft = "saved_draft", UnderReview = "under_review", Returned = "returned",
        Accepted = "accepted", Approval = "approval", Approved = "approved", Archived = "archived", Deleted = "deleted";

    public static string Title(string status) => status switch
    {
        Draft => "Черновик",
        SavedDraft => "Сохранённый черновик",
        UnderReview => "На проверке",
        Returned => "На доработке",
        Accepted => "Принят",
        Approval => "На согласовании",
        Approved => "Согласован",
        Archived => "Архивный",
        Deleted => "Удалённый",
        _ => status
    };

    /// <summary>
    /// Порядок продвижения документа к утверждению: отправка на проверку, принятие, согласование, утверждение.
    /// По нему поле с <c>x-required-for-status</c> обязательно при переходе в этот статус и дальше.
    /// </summary>
    public static int Progress(string status) => status switch
    {
        UnderReview => 1,
        Accepted => 2,
        Approval => 3,
        Approved => 4,
        _ => 0
    };
}
