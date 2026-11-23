using System.Text.Json;

namespace RpdFos.Web.Models;

// Контракт API документов (Features/Documents) на стороне клиента.

/// <param name="FromApproval">Документ на проверке вернулся с согласования (приходит методисту и администратору).</param>
public sealed record DocumentListItem(Guid Id, string Title, string Type, string Status, string StatusTitle, bool IsOutdated,
    string DisciplineCode, string DisciplineName, PlanItem? Plan, int TemplateRevision, string AuthorName, DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt, int Version = 0, bool CanDelete = false, ReturnedFromApproval? FromApproval = null);

/// <summary>Кто и когда вернул документ с согласования на проверку.</summary>
public sealed record ReturnedFromApproval(string By, DateTimeOffset At);

/// <param name="AssignedTo">За кем дисциплина закреплена (этап 7, A10); null — ни за кем.</param>
/// <param name="CanCreate">Пользователь может создать документ: закреплений нет, он закреплён, это методист или администратор.</param>
/// <param name="DocumentStatus">
/// Статус показанного документа дисциплины (этап 7, A11): у дисциплины с согласованным документом новый создаётся только на его
/// основе.
/// </param>
/// <param name="RpdId">
/// Для шаблона ФОС (этап 7, B3) — РПД, по которой ФОС создаётся, её автор и статус; null — у дисциплины нет РПД, по которой можно
/// создать ФОС.
/// </param>
public sealed record DisciplineOption(Guid Id, string Code, string Name, Guid? DocumentId, string? DocumentAuthor, bool CanOpen,
    string? AssignedTo = null, bool CanCreate = true, string? DocumentStatus = null, Guid? RpdId = null, string? RpdAuthor = null,
    string? RpdStatus = null);

public sealed record DocumentItem(Guid Id, string Type, string Title, string Status, string StatusTitle, bool IsOutdated, int Version,
    Guid TemplateId, int TemplateRevision, Guid DisciplineId, string DisciplineCode, string DisciplineName, Guid AuthorId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SubmittedAt);

/// <summary>
/// Что пользователь может сделать с документом: отправить, проверить (вернуть, принять), оставлять и разрешать замечания.
/// <paramref name="Comment"/> — методист на проверке и согласующий до своей отметки; <paramref name="ResolveApprovalComments"/> —
/// методист на проверке снимает и замечания согласующих (этап 7, A2). <paramref name="ReturnForCorrection"/> — методист
/// возвращает согласованный документ на исправление; <paramref name="ForceTargets"/> — статусы, в которые администратор
/// переводит документ принудительно (null — не администратор), этап 7, A8. <paramref name="Archive"/> — автор, методист или
/// администратор отправляют согласованный документ в архив; <paramref name="Transfer"/> — методист или администратор
/// передают документ другому автору (этап 7, A9). <paramref name="CreateBasedOn"/> — «Создать на основе» в согласованном и
/// архивном документе: автор, методист, администратор и закреплённый за дисциплиной (этап 7, A11). <paramref name="CreateFos"/> —
/// «Создать ФОС» в РПД, <paramref name="RefreshFromRpd"/> — «Обновить из РПД» в ФОС, когда данные РПД изменились (этап 7, B3).
/// </summary>
public sealed record DocumentActions(bool Submit, bool Review, bool ResolveComments, bool Print, bool CorrectPlan = false,
    bool TakeForReview = false, bool ResolveOwnComments = false, bool Delete = false, bool Approve = false,
    bool Comment = false, bool ResolveApprovalComments = false, bool ReturnForCorrection = false,
    List<StatusOption>? ForceTargets = null, bool Archive = false, bool Transfer = false, bool CreateBasedOn = false,
    bool CreateFos = false, bool RefreshFromRpd = false);

/// <summary>Статус и его название для списка «Сменить статус».</summary>
public sealed record StatusOption(string Status, string Title);

/// <summary>
/// Согласование документа (этап 6, B2–B3): места электронного согласования и что может зритель — назначить или
/// заменить подписанта, запустить согласование принятого документа. <paramref name="Returned"/> — прежний круг
/// закончился возвратом: кто, когда, с каким комментарием (методисту и администратору). <paramref name="Corrected"/> —
/// согласованный документ вернули на исправление: кто, когда, причина (этап 7, A8). <paramref name="Cancelled"/> — круг отменён
/// возвратом на исправление или принудительной сменой статуса (этап 8, A1): строки круга показываются отменёнными.
/// </summary>
public sealed record ApprovalState(
    List<ApprovalSeatItem> Seats, bool CanAssign, bool CanStart, ApprovalReturn? Returned, ApprovalReturn? Corrected = null,
    ApprovalReturn? Cancelled = null);

/// <summary>Место согласования: назначенный, его отметка и время, комментарий возврата.</summary>
/// <param name="CanSign">У назначенного есть действующая учётная запись с ролью «Утверждающий».</param>
/// <param name="Mine">Назначен текущий пользователь.</param>
public sealed record ApprovalSeatItem(string Role, string Title, Guid? SignatoryId, string? SignatoryName, string? Position,
    string Status, string StatusTitle, DateTimeOffset? MarkedAt, string? Comment, bool CanSign, bool Mine);

public sealed record ApprovalReturn(string? By, DateTimeOffset At, string? Comment);

/// <summary>Документ в очереди согласующего (S-31).</summary>
public sealed record ApprovalQueueItem(Guid Id, string Type, string DisciplineCode, string DisciplineName, string AuthorName,
    PlanItem? Plan, DateTimeOffset? SentAt);

/// <summary>
/// Документ в списке «Отмеченные мной» (S-31): статус документа сейчас и отметка пользователя в текущем круге —
/// <paramref name="Mark"/> (<c>approved</c> или <c>rejected</c>), её подпись и время.
/// </summary>
public sealed record MarkedApprovalItem(Guid Id, string Type, string DisciplineCode, string DisciplineName, string AuthorName,
    string Status, string StatusTitle, string Mark, string MarkTitle, DateTimeOffset? MarkedAt);

/// <summary>Исправление поля из учебного плана: место, было, стало, кто и когда.</summary>
public sealed record PlanCorrection(string Path, JsonElement? Before, JsonElement? After, string? ChangedBy, DateTimeOffset ChangedAt);

/// <param name="BasedOn">Документ создан на основе другого (этап 7, A11): основа и список неперенесённого; иначе null.</param>
/// <param name="LinkedRpd">У ФОС (этап 7, B3): связанная РПД, снимок и пометка «РПД изменилась»; у остальных null.</param>
/// <param name="LinkedFos">У РПД (B3): созданные по ней ФОС, новейшие первыми; у остальных null.</param>
/// <param name="AcceptUnassigned">Места, подписанты которых приём не найдёт (этап 8, A1) — для подтверждения «Принять»; null — приём недоступен.</param>
public sealed record DocumentDetails(DocumentItem Document, JsonElement Payload, JsonElement Schema, DocumentMetadata Metadata,
    bool Editable, DocumentActions Actions, JsonElement? TemplateDefaults = null, List<PlanCorrection>? PlanCorrections = null,
    Guid? ViewerId = null, ApprovalState? Approval = null, DocumentBasis? BasedOn = null, LinkedRpdItem? LinkedRpd = null,
    List<LinkedFosItem>? LinkedFos = null, List<string>? AcceptUnassigned = null);

/// <summary>
/// Связанная РПД ФОС (этап 7, B3): название и статус сейчас, открывается ли зрителю, версия и время снимка. <paramref name="Changed"/> —
/// данные РПД изменились после снимка («РПД изменилась»); <paramref name="Deleted"/> — РПД удалена, обновлять не из чего.
/// </summary>
public sealed record LinkedRpdItem(Guid Id, string? Title, string Status, string StatusTitle, string? AuthorName, bool CanOpen,
    bool Deleted, bool Changed, int SnapshotVersion, int? CurrentVersion, DateTimeOffset? SnapshotAt, bool CrossCheck = false);

/// <summary>ФОС, созданный по РПД (этап 7, B3); <paramref name="Discrepancies"/> — предупреждения сквозной проверки (B8), null — не считались.</summary>
public sealed record LinkedFosItem(Guid Id, string Title, string Status, string StatusTitle, string AuthorName, bool CanOpen,
    int? Discrepancies = null);

/// <summary>
/// Основа документа («Создать на основе», этап 7, A11): название и статус сейчас (<c>null</c> — основа удалена), открывается ли
/// она зрителю и что из неё не перенесено — по тексту на строку.
/// </summary>
public sealed record DocumentBasis(Guid Id, string Title, string? Status, string? StatusTitle, bool CanOpen, List<string> NotTransferred);

public sealed record DocumentMetadata(List<SectionState> Sections, List<string> ReadOnlyPaths, List<string> HiddenPaths,
    List<CompetenceCandidate> CompetenceCandidates, PlanDisciplineChoices? PlanDisciplines,
    Dictionary<string, JsonElement?>? PlanValues = null, Dictionary<string, string>? NoCandidatesTexts = null);

/// <summary>Дисциплины и практики плана раньше и позже дисциплины — кандидаты в пре- и постреквизиты.</summary>
public sealed record PlanDisciplineChoices(List<PlanDisciplineCandidate> Earlier, List<PlanDisciplineCandidate> Later);

public sealed record PlanDisciplineCandidate(Guid Id, string Code, string Name, List<int> Semesters);

public sealed record SectionState(string Key, string Title, bool Visible, string? StubText, string Fill, List<FieldError> Missing,
    List<FieldError> Warnings);

/// <summary>Замечание проверки: место, сообщение и уровень — ошибка блокирует отправку, предупреждение нет.</summary>
public sealed record FieldError(string Path, string Message, string Level = "error", string? Rule = null)
{
    public bool IsWarning => Level == "warning";
}

public sealed record CompetenceCandidate(Guid Id, string Code, string Formulation, string Category, List<IndicatorCandidate> Indicators);

public sealed record IndicatorCandidate(Guid Id, string Code, string Text, string? Type);

/// <summary>Раздел проверен без сохранения (этап 5.2): состояние всех разделов и ошибки черновика.</summary>
public sealed record SectionChecked(List<SectionState> Sections, List<string> HiddenPaths, List<FieldError> Errors);

/// <summary>Раздел сохранён; с ним — состояние всех разделов и история исправлений полей из плана (если исправлял).</summary>
public sealed record SectionSaved(int Version, string Status, string StatusTitle, SectionState Section, DateTimeOffset SavedAt,
    List<SectionState>? Sections = null, List<PlanCorrection>? PlanCorrections = null, List<string>? HiddenPaths = null,
    JsonElement? Payload = null);

public sealed record DocumentSaved(int Version, string Status, string StatusTitle, DateTimeOffset SavedAt);

/// <summary>Ответ об ошибке API документов: причины по полям — путь и сообщение.</summary>
/// <param name="ConfirmReturn">Исправление принятого документа вернёт его автору — сервер ждёт подтверждения (этап 5.2).</param>
public sealed record DocumentProblem(string? Title, string? Detail, List<FieldError>? Errors, Guid? DocumentId, bool ConfirmReturn = false);

public static class SectionFills
{
    public const string Complete = "complete", Incomplete = "incomplete", Empty = "empty", NotApplicable = "not_applicable";
}

public static class DocumentTitles
{
    public static readonly (string Code, string Title)[] Statuses =
    [
        ("draft", "Черновик"), ("saved_draft", "Сохранённый черновик"), ("under_review", "На проверке"),
        ("returned", "На доработке"), ("accepted", "Принят"), ("approval", "На согласовании"), ("approved", "Согласован"),
        ("archived", "Архивный")
    ];

    /// <summary>
    /// Подсказка у значка «устарел»: признак ставят две причины — вышла новая редакция шаблона (FR-DOC-12) и согласован документ,
    /// созданный на основе этого, для той же дисциплины (этап 7, A11).
    /// </summary>
    public const string OutdatedHint = "Устарел: вышла новая редакция шаблона или согласован документ, созданный на основе этого";
}

/// <summary>
/// Статус сменился: новая версия и статус; при отправке — предупреждения полной проверки. Тот же ответ читается и у
/// действий согласования — там предупреждений нет.
/// </summary>
public sealed record DocumentReviewed(int Version, string Status, string StatusTitle, List<FieldError>? Warnings = null,
    string? Notice = null);

/// <summary>
/// Замечание к полю: кто и когда оставил, разрешено ли и кем. <paramref name="FromApproval"/> — оставлено на согласовании
/// согласующим (этап 7, A2).
/// </summary>
public sealed record CommentItem(Guid Id, string FieldPath, string Text, Guid AuthorId, string AuthorName, DateTimeOffset CreatedAt,
    bool IsResolved, string? ResolvedByName, DateTimeOffset? ResolvedAt, bool FromApproval = false);

/// <summary>Задача формирования печатной формы: статус, файл (если сформирован) и отчёт.</summary>
public sealed record GenerationItem(Guid TaskId, string Format, string Status, string? ErrorMessage, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, Guid? FileId, string? FileName, long? SizeBytes, DateTimeOffset? ExpiresAt, List<string> Warnings,
    List<string> Unfilled)
{
    public bool InProgress => Status is "queued" or "running";
}

/// <summary>Ответ «Предложить» оценочные средства (этап 7, B6): строки раздела <c>evaluation_means</c> и сообщение автору.</summary>
public sealed record MeansSuggestionResult(System.Text.Json.Nodes.JsonArray Rows, string Notice);
