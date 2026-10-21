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

    /// <summary>Вышла новая редакция шаблона — ортогонально статусу (FR-DOC-12).</summary>
    public bool IsOutdated { get; set; }

    public Guid TemplateId { get; set; }

    /// <summary>Редакция шаблона на момент создания. В проекте — <c>template_version</c>, по аналогии с revision шаблона.</summary>
    public int TemplateRevision { get; set; }

    public Guid AuthorId { get; set; }
    public Guid DisciplineId { get; set; }

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

    /// <summary>Возврат на доработку методистом. Возвращает прежний статус.</summary>
    public string ReturnForRework()
    {
        Require(DocumentTransitions.CanReview(Status), DocumentTransitions.ReturnAction);
        return Move(DocumentStatuses.Returned);
    }

    /// <summary>Приём методистом. Возвращает прежний статус.</summary>
    public string Accept()
    {
        Require(DocumentTransitions.CanReview(Status), DocumentTransitions.AcceptAction);
        return Move(DocumentStatuses.Accepted);
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

/// <summary>Разрешённые действия с документом по статусу и роли — в одном месте, а не в эндпоинтах.</summary>
public static class DocumentTransitions
{
    public const string EditAction = "изменить", SaveAction = "сохранить", DeleteAction = "удалить",
        SubmitAction = "отправить на проверку", ReturnAction = "вернуть на доработку", AcceptAction = "принять",
        CommentAction = "оставить замечание", ResolveAction = "отметить замечание разрешённым";

    /// <summary>
    /// Автор правит черновик, сохранённый черновик и документ на доработке; на проверке и после приёма документ
    /// для него закрыт. Методист правит документ на проверке, не меняя статуса (FR-REV-11).
    /// </summary>
    public static bool CanEdit(string status, DocumentRole role) => role switch
    {
        DocumentRole.Author => status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft or DocumentStatuses.Returned,
        _ => status is DocumentStatuses.UnderReview
    };

    /// <summary>«Сохранить» — там же, где правка: на доработке и на проверке это только снимок версии.</summary>
    public static bool CanSave(string status, DocumentRole role) => CanEdit(status, role);

    public static bool CanDelete(string status) => status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft;

    /// <summary>Отправка на проверку — из черновика, сохранённого черновика и с доработки (повторная).</summary>
    public static bool CanSubmit(string status) =>
        status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft or DocumentStatuses.Returned;

    /// <summary>Возврат, приём и замечания методиста — пока документ на проверке.</summary>
    public static bool CanReview(string status) => status is DocumentStatuses.UnderReview;

    /// <summary>Автор отмечает замечания разрешёнными, пока документ на доработке.</summary>
    public static bool CanResolveComments(string status) => status is DocumentStatuses.Returned;

    public static string Refusal(string status, string action) =>
        $"Документ в статусе «{DocumentStatuses.Title(status)}» нельзя {action}.";
}

/// <summary>
/// Замечание методиста к полю документа (таблица document_comments). Не удаляется — это история проверки;
/// автор отмечает его разрешённым.
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
