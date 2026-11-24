using RpdFos.Core.Documents;

namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Документ — РПД или ФОС по шаблону для дисциплины плана (таблица documents, раздел 2.4 ВКР). Содержимое —
/// <see cref="Payload"/> (jsonb) по снимку схемы шаблона; часы и компетенции дублируются в нормализованных
/// таблицах для сверки с планом.
/// </summary>
public class Document : IVersioned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = TemplateTypes.Rpd;
    public string Status { get; set; } = DocumentStatuses.Draft;

    /// <summary>
    /// Вышла новая редакция шаблона или согласован документ, созданный на основе этого, для той же дисциплины (этап 7, A11) —
    /// ортогонально статусу (FR-DOC-12). Версию документа признак не меняет (<c>VersioningInterceptor</c>).
    /// </summary>
    public bool IsOutdated { get; set; }

    public Guid TemplateId { get; set; }

    /// <summary>Редакция шаблона на момент создания. В проекте — <c>template_version</c>, по аналогии с revision шаблона.</summary>
    public int TemplateRevision { get; set; }

    public Guid AuthorId { get; set; }
    public Guid DisciplineId { get; set; }

    /// <summary>
    /// Документ, на основе которого создан этот: из него перенесён ввод автора (колонка <c>based_on_document_id</c> — дополнение
    /// модели данных, этап 7, A11; решение 4). Пусто — документ создан с нуля. Когда документ с основой той же дисциплины
    /// согласован, основа получает <see cref="IsOutdated"/> (расширение FR-DOC-12).
    /// </summary>
    public Guid? BasedOnDocumentId { get; set; }

    /// <summary>«Тип + дисциплина + год».</summary>
    public string Title { get; set; } = "";

    public string Payload { get; set; } = "{}";

    /// <summary>Оптимистичная блокировка (в проекте — <c>current_version</c>).</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Изменено содержимое. Сохранённый черновик снова становится черновиком: изменения после «Сохранить» ещё
    /// не сохранены. На доработке и на проверке статус не меняется. Возвращает прежний статус, если он сменился.
    /// </summary>
    public string? Edit(string payload)
    {
        Require(DocumentTransitions.CanEdit(Status, DocumentRole.Author) || DocumentTransitions.CanEdit(Status, DocumentRole.Reviewer),
            DocumentTransitions.EditAction);
        Payload = payload;
        return Status == DocumentStatuses.SavedDraft ? Move(DocumentStatuses.Draft) : null;
    }

    /// <summary>Исправление полей из плана методистом или администратором: статус не меняется (этап 5.1).</summary>
    public void CorrectPlanFields(string payload)
    {
        Require(DocumentTransitions.CanCorrectPlanFields(Status), DocumentTransitions.EditAction);
        Payload = payload;
    }

    /// <summary>
    /// «Сохранить»: черновик становится сохранённым черновиком; на доработке и на проверке статус не меняется —
    /// сохранение даёт только снимок версии. Возвращает прежний статус, если он сменился.
    /// </summary>
    public string? Save()
    {
        Require(DocumentTransitions.CanSave(Status, DocumentRole.Author) || DocumentTransitions.CanSave(Status, DocumentRole.Reviewer),
            DocumentTransitions.SaveAction);
        return Status == DocumentStatuses.Draft ? Move(DocumentStatuses.SavedDraft) : null;
    }

    /// <summary>Отправка на проверку (в том числе повторная — с доработки). Возвращает прежний статус.</summary>
    public string Submit(DateTimeOffset now)
    {
        Require(DocumentTransitions.CanSubmit(Status), DocumentTransitions.SubmitAction);
        SubmittedAt = now;
        return Move(DocumentStatuses.UnderReview);
    }

    /// <summary>
    /// Методист берёт документ на проверку из статусов автора (этап 6, A1): автор без доступа к системе или ошибка при
    /// отправке. Время отправки — время взятия. Возвращает прежний статус.
    /// </summary>
    public string TakeForReview(DateTimeOffset now)
    {
        Require(DocumentTransitions.CanTakeForReview(Status), DocumentTransitions.TakeAction);
        SubmittedAt = now;
        return Move(DocumentStatuses.UnderReview);
    }

    /// <summary>Возврат на доработку методистом. Возвращает прежний статус.</summary>
    public string ReturnForRework()
    {
        Require(DocumentTransitions.CanReview(Status), DocumentTransitions.ReturnAction);
        return Move(DocumentStatuses.Returned);
    }

    /// <summary>
    /// Возврат автору принятого документа, который исправление данных плана сделало неполным (этап 5.2). Возвращает
    /// прежний статус.
    /// </summary>
    public string ReturnAfterPlanCorrection()
    {
        Require(DocumentTransitions.CanReturnAfterPlanCorrection(Status), DocumentTransitions.ReturnAction);
        return Move(DocumentStatuses.Returned);
    }

    /// <summary>Приём методистом. Возвращает прежний статус.</summary>
    public string Accept()
    {
        Require(DocumentTransitions.CanReview(Status), DocumentTransitions.AcceptAction);
        return Move(DocumentStatuses.Accepted);
    }

    /// <summary>
    /// Старт согласования: принятый документ уходит согласующим, когда подписанты всех электронных мест назначены
    /// (UC-07, шаг 3). Возвращает прежний статус.
    /// </summary>
    public string StartApproval()
    {
        Require(DocumentTransitions.CanStartApproval(Status), DocumentTransitions.StartApprovalAction);
        return Move(DocumentStatuses.Approval);
    }

    /// <summary>Согласие отметили все электронные согласующие (UC-08). Возвращает прежний статус.</summary>
    public string Approve(DateTimeOffset now)
    {
        Require(DocumentTransitions.CanSign(Status), DocumentTransitions.ApproveAction);
        ApprovedAt = now;
        return Move(DocumentStatuses.Approved);
    }

    /// <summary>
    /// Возврат с согласования — методисту, в «На проверке»: он решает, исправить самому или вернуть автору (UC-09).
    /// Возвращает прежний статус.
    /// </summary>
    public string ReturnFromApproval()
    {
        Require(DocumentTransitions.CanSign(Status), DocumentTransitions.RejectAction);
        return Move(DocumentStatuses.UnderReview);
    }

    /// <summary>
    /// Методист возвращает согласованный документ на проверку — исправить его иначе нельзя: правки после согласования
    /// закрыты для всех (этап 7, A8, решение 2). Время согласования сбрасывается; снимок согласованной версии остаётся в
    /// истории версий. Возвращает прежний статус.
    /// </summary>
    public string ReturnForCorrection()
    {
        Require(DocumentTransitions.CanReturnForCorrection(Status), DocumentTransitions.ReturnForCorrectionAction);
        ApprovedAt = null;
        return Move(DocumentStatuses.UnderReview);
    }

    /// <summary>
    /// Принудительная смена статуса администратором — только назад (этап 7, A8; FR-ADM-04). Согласия у документа больше
    /// нет — время согласования сбрасывается. Документ, пришедший на проверку не отправкой, получает время отправки: по
    /// нему он встаёт в очередь проверки. Возвращает прежний статус.
    /// </summary>
    public string ForceStatus(string target, DateTimeOffset now)
    {
        Require(DocumentTransitions.CanForceStatus(Status, target), DocumentTransitions.ForceStatusAction);
        ApprovedAt = null;
        if (target == DocumentStatuses.UnderReview) SubmittedAt ??= now;
        return Move(target);
    }

    /// <summary>
    /// «В архив» (этап 7, A9, решение 3): согласованный документ уходит в архив только явно — автором, методистом или
    /// администратором. Архивный документ открыт только для чтения и дисциплину не занимает. Возвращает прежний статус.
    /// </summary>
    public string Archive()
    {
        Require(DocumentTransitions.CanArchive(Status), DocumentTransitions.ArchiveAction);
        return Move(DocumentStatuses.Archived);
    }

    /// <summary>
    /// Передача документа другому автору (FR-DOC-15; этап 7, A9): меняется только владелец — статус, содержимое и составители
    /// в нём прежние. Возвращает прежнего автора.
    /// </summary>
    public Guid TransferTo(Guid newAuthorId)
    {
        Require(DocumentTransitions.CanTransfer(Status), DocumentTransitions.TransferAction);
        var previous = AuthorId;
        AuthorId = newAuthorId;
        return previous;
    }

    /// <summary>Мягкое удаление: статус и флаг — вместе. Возвращает прежний статус.</summary>
    public string Delete(DateTimeOffset now)
    {
        Require(DocumentTransitions.CanDelete(Status), DocumentTransitions.DeleteAction);
        IsDeleted = true;
        DeletedAt = now;
        return Move(DocumentStatuses.Deleted);
    }

    private string Move(string status)
    {
        var from = Status;
        Status = status;
        return from;
    }

    // Эндпоинты проверяют переход заранее и отвечают 409; здесь — защита от ошибки в коде.
    private void Require(bool allowed, string action)
    {
        if (!allowed) throw new InvalidOperationException(DocumentTransitions.Refusal(Status, action));
    }
}

/// <summary>Кем пользователь приходится документу: автор или проверяющий методист.</summary>
public enum DocumentRole { Author, Reviewer }

/// <summary>
/// Что можно менять при правке: содержимое без полей из плана (автор), содержимое с полями из плана (методист на
/// проверке) или только поля из плана (методист после приёма, администратор) — этап 5.1.
/// </summary>
public enum DocumentEditScope { Content, ContentAndPlan, PlanOnly }

/// <summary>Разрешённые действия с документом по статусу и роли — в одном месте, а не в эндпоинтах.</summary>
public static class DocumentTransitions
{
    public const string EditAction = "изменить", SaveAction = "сохранить", DeleteAction = "удалить",
        SubmitAction = "отправить на проверку", ReturnAction = "вернуть на доработку", AcceptAction = "принять",
        CommentAction = "оставить замечание", ResolveAction = "отметить замечание разрешённым",
        TakeAction = "взять на проверку", StartApprovalAction = "отправить на согласование", ApproveAction = "согласовать",
        RejectAction = "вернуть с согласования", AssignAction = "назначить подписанта",
        ReturnForCorrectionAction = "вернуть на исправление", ForceStatusAction = "перевести в другой статус",
        ArchiveAction = "отправить в архив", TransferAction = "передать другому автору";

    /// <summary>
    /// Автор правит черновик, сохранённый черновик и документ на доработке; на проверке и после приёма документ
    /// для него закрыт. Методист правит документ на проверке, не меняя статуса (FR-REV-11).
    /// </summary>
    public static bool CanEdit(string status, DocumentRole role) => role switch
    {
        DocumentRole.Author => status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft or DocumentStatuses.Returned,
        _ => status is DocumentStatuses.UnderReview
    };

    /// <summary>
    /// Поля из учебного плана исправляют методист и администратор — на проверке и после приёма: иногда дешевле
    /// поправить готовый документ, чем источник. Автору они закрыты всегда.
    /// </summary>
    public static bool CanCorrectPlanFields(string status) => status is DocumentStatuses.UnderReview or DocumentStatuses.Accepted;

    /// <summary>
    /// Объём правки для пользователя: методист на проверке — всё, включая поля плана; автор — своё содержимое;
    /// методист после приёма и администратор — только поля плана. null — править нельзя.
    /// </summary>
    /// <param name="isCorrector">Методист или администратор.</param>
    public static DocumentEditScope? EditScope(string status, bool isAuthor, bool isReviewer, bool isCorrector) =>
        isReviewer && CanEdit(status, DocumentRole.Reviewer) ? DocumentEditScope.ContentAndPlan
        : isAuthor && CanEdit(status, DocumentRole.Author) ? DocumentEditScope.Content
        : isCorrector && CanCorrectPlanFields(status) ? DocumentEditScope.PlanOnly
        : null;

    /// <summary>«Сохранить» — там же, где правка: на доработке и на проверке это только снимок версии.</summary>
    public static bool CanSave(string status, DocumentRole role) => CanEdit(status, role);

    public static bool CanDelete(string status) => status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft;

    /// <summary>Отправка на проверку — из черновика, сохранённого черновика и с доработки (повторная).</summary>
    public static bool CanSubmit(string status) =>
        status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft or DocumentStatuses.Returned;

    /// <summary>
    /// «Принят → На доработке» — только по исправлению данных плана, после которого полная проверка находит ошибки:
    /// автор обязан заполнить то, что появилось (этап 5.2).
    /// </summary>
    public static bool CanReturnAfterPlanCorrection(string status) => status is DocumentStatuses.Accepted;

    /// <summary>
    /// Методист берёт на проверку документ из статусов автора — черновик, сохранённый черновик, доработка (этап 6, A1).
    /// Отступление от FR-ADM-04 (принудительная смена статуса — только администратору): узкое право методиста; второе —
    /// возврат согласованного документа на исправление (<see cref="CanReturnForCorrection"/>, этап 7, A8).
    /// </summary>
    public static bool CanTakeForReview(string status) =>
        status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft or DocumentStatuses.Returned;

    /// <summary>Возврат, приём и замечания методиста — пока документ на проверке.</summary>
    public static bool CanReview(string status) => status is DocumentStatuses.UnderReview;

    /// <summary>Автор отмечает замечания разрешёнными, пока документ на доработке.</summary>
    public static bool CanResolveComments(string status) => status is DocumentStatuses.Returned;

    /// <summary>
    /// Замечания к полям оставляют методист — на проверке — и согласующий — на согласовании, пока его строка ждёт
    /// отметки (этап 7, A2): после отметки решение принято, и замечание уже ни на что не повлияет.
    /// </summary>
    /// <param name="awaitingMark">У согласующего есть строка согласования документа, ждущая его отметки.</param>
    public static bool CanComment(string status, bool isReviewer, bool awaitingMark) =>
        isReviewer && CanReview(status) || awaitingMark && CanSign(status);

    /// <summary>
    /// Замечание разрешает автор на доработке — любое. Методист на проверке — своё (этап 6, A2: документ, взятый с
    /// доработки, иначе не принять) и оставленное на согласовании: после возврата он решает, учесть замечание
    /// согласующего самому или вернуть документ автору (UC-09, 3а). Согласующий — своё, пока его строка ждёт отметки
    /// (этап 7, A2).
    /// </summary>
    /// <param name="awaitingMark">У согласующего есть строка согласования документа, ждущая его отметки.</param>
    /// <param name="fromApproval">Замечание оставлено на согласовании — согласующим.</param>
    public static bool CanResolveComment(
        string status, bool isAuthor, bool isReviewer, bool awaitingMark, bool ownComment, bool fromApproval) =>
        isAuthor && CanResolveComments(status)
        || isReviewer && (ownComment || fromApproval) && CanReview(status)
        || awaitingMark && ownComment && CanSign(status);

    /// <summary>Согласование стартует из «Принят»: при приёме — само, после ручного назначения — кнопкой (этап 6, B2).</summary>
    public static bool CanStartApproval(string status) => status is DocumentStatuses.Accepted;

    /// <summary>Согласующие отмечают согласие или возвращают документ, пока он на согласовании (UC-08, UC-09).</summary>
    public static bool CanSign(string status) => status is DocumentStatuses.Approval;

    /// <summary>
    /// Подписанта назначают и заменяют, пока его отметки ждут: в «Принят» — тех, кого не нашло правило, на
    /// согласовании — отсутствующего (FR-REV-13).
    /// </summary>
    public static bool CanAssignSignatory(string status) => status is DocumentStatuses.Accepted or DocumentStatuses.Approval;

    /// <summary>
    /// Печатную форму формирует автор — в любом статусе, методист — с проверки и дальше, согласующий — на согласовании и
    /// после него: предварительный PDF он формирует сам (этап 6, B2–B3). Архивный документ печатают те же участники, что
    /// и согласованный (этап 7, A9).
    /// </summary>
    public static bool CanPrint(string status, bool isAuthor, bool isReviewer, bool isSignatory) =>
        isAuthor
        || isReviewer && status is DocumentStatuses.UnderReview or DocumentStatuses.Accepted or DocumentStatuses.Approval
            or DocumentStatuses.Approved or DocumentStatuses.Archived
        || isSignatory && status is DocumentStatuses.Approval or DocumentStatuses.Approved or DocumentStatuses.Archived;

    /// <summary>
    /// «В архив» — только из «Согласован», явным действием автора, методиста или администратора (этап 7, A9, решение 3):
    /// сам документ в архив не уходит, а согласованный преемник лишь помечает прежний документ «устарел» (A11).
    /// </summary>
    public static bool CanArchive(string status) => status is DocumentStatuses.Approved;

    /// <summary>
    /// Передача документа другому автору — в любом статусе, кроме «Удалённый» (FR-DOC-15; этап 7, A9): документ нужен и
    /// после согласования — для печати и «Создать на основе».
    /// </summary>
    public static bool CanTransfer(string status) => status != DocumentStatuses.Deleted;

    /// <summary>
    /// «Создать на основе» в самом документе (S-13; этап 7, A11, решение 4): согласованный и архивный — из них делают
    /// следующую редакцию для той же дисциплины и документ для дисциплины другого плана. Основой при создании (S-11) служит
    /// документ любого статуса, кроме удалённого.
    /// </summary>
    public static bool CanBaseNew(string status) => status is DocumentStatuses.Approved or DocumentStatuses.Archived;

    /// <summary>
    /// ФОС создаётся по РПД в любом статусе, кроме удалённого и архивного (этап 7, B3, решение 8): ФОС начинают, пока РПД ещё
    /// пишется, а отправить его можно, когда РПД на проверке или дальше (B8).
    /// </summary>
    public static bool CanCreateFosFrom(string rpdStatus) => rpdStatus is not (DocumentStatuses.Deleted or DocumentStatuses.Archived);

    /// <summary>
    /// «Обновить из РПД» — автор ФОС в статусах правки и методист на проверке (этап 7, B3, решение 8): те же, в которых
    /// правят содержимое. После отправки и приёма снимок закрыт, как и остальное содержимое.
    /// </summary>
    public static bool CanRefreshFromRpd(string status, bool isAuthor, bool isReviewer) =>
        isAuthor && CanEdit(status, DocumentRole.Author) || isReviewer && CanEdit(status, DocumentRole.Reviewer);

    public const string RefreshFromRpdAction = "обновить из РПД";

    /// <summary>
    /// «Вернуть на исправление» — методист возвращает согласованный документ на проверку (этап 7, A8, решение 2). Другого
    /// пути к правке согласованного документа нет: после согласования она закрыта для всех. Время согласования
    /// сбрасывается, следующий приём начинает новый круг.
    /// </summary>
    public static bool CanReturnForCorrection(string status) => status is DocumentStatuses.Approved;

    /// <summary>
    /// Статусы, в которые администратор переводит документ принудительно (FR-ADM-04; этап 7, A8) — только назад. В «Принят»,
    /// «На согласовании», «Согласован» и «Архивный» принудительно не переводят: они наступают только своим порядком —
    /// приёмом, кругом согласования, явным архивированием.
    /// </summary>
    public static readonly IReadOnlyList<string> ForceTargets =
        [DocumentStatuses.SavedDraft, DocumentStatuses.Returned, DocumentStatuses.UnderReview];

    /// <summary>
    /// Принудительная смена — из любого статуса, кроме «Удалённый», в один из <see cref="ForceTargets"/>, но не в тот же
    /// статус: смена без перемены записала бы в историю переход из статуса в него же.
    /// </summary>
    public static bool CanForceStatus(string from, string to) =>
        from != DocumentStatuses.Deleted && from != to && ForceTargets.Contains(to);

    /// <summary>Статусы, в которые документ из <paramref name="from"/> переводят принудительно, — для кнопки «Сменить статус».</summary>
    public static IReadOnlyList<string> ForceTargetsFrom(string from) => ForceTargets.Where(to => CanForceStatus(from, to)).ToList();

    /// <summary>Статусы завершённого документа: «Согласован», «Архивный», «Удалённый».</summary>
    public static readonly IReadOnlyList<string> FinishedStatuses =
        [DocumentStatuses.Approved, DocumentStatuses.Archived, DocumentStatuses.Deleted];

    /// <summary>
    /// Незавершённый документ — не «Согласован», не «Архивный», не «Удалённый». На дисциплину и линию редакций шаблона
    /// приходится не больше одного незавершённого (уточнение планировщика, этап 7): создание и переход, которые дали бы
    /// второй, — 409. Возврат согласованного документа на исправление и принудительная смена статуса возвращают
    /// завершённый документ в работу.
    /// </summary>
    public static bool IsUnfinished(string status) => !FinishedStatuses.Contains(status);

    public static string Refusal(string status, string action) =>
        $"Документ в статусе «{DocumentStatuses.Title(status)}» нельзя {action}.";
}

/// <summary>
/// Замечание к полю документа (таблица document_comments): методиста на проверке или согласующего на согласовании
/// (этап 7, A2). Не удаляется — это история проверки; автор отмечает его разрешённым.
/// </summary>
public class DocumentComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid AuthorId { get; set; }

    /// <summary>JSON Pointer поля в содержимом: /thematic_plan/0/hours. В проекте — JSONPath; Pointer — как в ошибках проверки.</summary>
    public string FieldPath { get; set; } = "";

    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsResolved { get; set; }

    /// <summary>Кто отметил разрешённым — дополнение к проекту (задание этапа 5).</summary>
    public Guid? ResolvedBy { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// Запись списка, к которой относилось замечание, удалена (этап 8, A14). <see cref="FieldPath"/> остаётся местом записи
    /// на момент удаления — по нему замечание подписывается, но у полей оно не показывается: номер мог занять другая запись.
    /// </summary>
    public bool IsRecordDeleted { get; set; }

    /// <summary>Отметить разрешённым; false — уже разрешено.</summary>
    public bool Resolve(Guid by, DateTimeOffset at)
    {
        if (IsResolved) return false;
        (IsResolved, ResolvedBy, ResolvedAt) = (true, by, at);
        return true;
    }
}

/// <summary>
/// Снимок версии документа (таблица document_versions). Новый снимок — при ручном сохранении, смене статуса и
/// утверждении; автосохранения перезаписывают последний рабочий снимок.
/// </summary>
public class DocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int VersionNumber { get; set; }
    public string Snapshot { get; set; } = "{}";
    public string HoursSnapshot { get; set; } = "[]";
    public string CompetencesSnapshot { get; set; } = "[]";
    public Guid SavedBy { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public string TriggerEvent { get; set; } = VersionTriggers.ManualSave;
}

public static class VersionTriggers
{
    public const string AutoSave = "auto_save", ManualSave = "manual_save", StatusChange = "status_change", Approved = "approved";
}

/// <summary>Часы документа по семестру (таблица document_hours) — нормализованная копия из payload для сверки с планом.</summary>
public class DocumentHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int Semester { get; set; }
    public int Lectures { get; set; }
    public int Practice { get; set; }
    public int LabHours { get; set; }
    public int SelfStudy { get; set; }
    public int Control { get; set; }
}

/// <summary>Привязка документа к компетенции и индикатору (таблица document_competence_links): хотя бы один ключ.</summary>
public class DocumentCompetenceLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid? CompetenceId { get; set; }
    public Guid? IndicatorId { get; set; }
}

/// <summary>Переход статуса документа (таблица document_status_history); при создании — из null.</summary>
public class DocumentStatusChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = DocumentStatuses.Draft;
    public Guid ChangedBy { get; set; }

    /// <summary>
    /// Комментарий к переходу. При возврате на доработку — общий комментарий методиста или число замечаний: сами
    /// замечания к полям обязательны, поэтому общий комментарий необязателен (отступление от проекта, этап 5).
    /// </summary>
    public string? Comment { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}
