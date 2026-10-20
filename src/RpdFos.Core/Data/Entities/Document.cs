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
