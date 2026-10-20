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
    /// не сохранены. Возвращает прежний статус, если он сменился.
    /// </summary>
    public string? Edit(string payload)
    {
        Require(DocumentTransitions.CanEdit(Status), DocumentTransitions.EditAction);
        Payload = payload;
        return Status == DocumentStatuses.SavedDraft ? Move(DocumentStatuses.Draft) : null;
    }

    /// <summary>«Сохранить»: сохранённый черновик. Возвращает прежний статус, если он сменился.</summary>
    public string? Save()
    {
        Require(DocumentTransitions.CanSave(Status), DocumentTransitions.SaveAction);
        return Status == DocumentStatuses.SavedDraft ? null : Move(DocumentStatuses.SavedDraft);
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

/// <summary>Разрешённые действия с документом по статусу — в одном месте, а не в эндпоинтах.</summary>
public static class DocumentTransitions
{
    public const string EditAction = "изменить", SaveAction = "сохранить", DeleteAction = "удалить";

    // Автор правит черновик, сохранённый черновик и документ, возвращённый на доработку.
    public static bool CanEdit(string status) =>
        status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft or DocumentStatuses.Returned;

    public static bool CanSave(string status) => status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft;

    public static bool CanDelete(string status) => status is DocumentStatuses.Draft or DocumentStatuses.SavedDraft;

    public static string Refusal(string status, string action) =>
        $"Документ в статусе «{DocumentStatuses.Title(status)}» нельзя {action}.";
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

    /// <summary>Обязателен при возврате на доработку (этап 5).</summary>
    public string? Comment { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}
