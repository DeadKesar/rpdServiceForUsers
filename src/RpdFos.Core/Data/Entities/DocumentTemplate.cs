namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Шаблон документа (таблица document_templates, раздел 2.4 ВКР, контекст Documents): тип, учебный план,
/// снимок структуры типа на момент создания, DOCX-шаблон оформления и сопоставление его закладок.
/// </summary>
public class DocumentTemplate : IVersioned
{
    // Первая редакция начинает свою линию редакций.
    public DocumentTemplate() => LineageId = Id;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = TemplateTypes.Rpd;
    public string Name { get; set; } = "";
    public string Status { get; set; } = TemplateStatuses.Empty;

    /// <summary>Оптимистичная блокировка: растёт при каждом сохранении. Редакция шаблона — <see cref="Revision"/>.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Редакция шаблона: 1, 2, … В проекте это <c>version</c>, но в коде <see cref="Version"/> занята
    /// оптимистичной блокировкой.
    /// </summary>
    public int Revision { get; set; } = 1;

    /// <summary>Общий для всех редакций одного шаблона: переименование не рвёт связь редакций.</summary>
    public Guid LineageId { get; set; }

    /// <summary>Версия учебного плана, для которой создан шаблон.</summary>
    public Guid? CurriculumPlanId { get; set; }

    /// <summary>JSON Schema содержимого — снимок из реестра типов в момент создания (jsonb).</summary>
    public string Schema { get; set; } = "{}";

    /// <summary>Лист согласования: места подписантов с правилами определения — снимок из реестра (jsonb).</summary>
    public string SignaturesConfig { get; set; } = "[]";

    /// <summary>DOCX-шаблон оформления с закладками (FR-TPL-13) — файл в uploaded_files.</summary>
    public Guid? PrintTemplateFileId { get; set; }

    /// <summary>Сопоставление закладок шаблона оформления: базовое имя → цель (jsonb).</summary>
    public string? PrintMapping { get; set; }

    /// <summary>Методист-создатель — запись справочника сотрудников.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class TemplateTypes
{
    // В прототипе типы документов — константы, а их структуры — файлы ядра (DocumentTypes/*.json).
    // Ведение структуры типов Администратором через интерфейс — перспектива.
    public const string Rpd = "rpd", Fos = "fos";
}

public static class TemplateStatuses
{
    // Шесть состояний шаблона с диаграммы состояний (рис. 2.6 ВКР).
    public const string Empty = "empty", Draft = "draft", SavedDraft = "saved_draft",
        Published = "published", Archived = "archived", Deleted = "deleted";
}

/// <summary>Разрешённые переходы статусов шаблона — в одном месте, а не в эндпоинтах.</summary>
public static class TemplateTransitions
{
    // Сверить с диаграммой состояний шаблона. Публикацию из «Пустой» на этапе 3
    // закроет финальная проверка шаблона (FR-TPL-11); пока она нужна для проверки среза.
    public static bool CanPublish(string status) =>
        status is TemplateStatuses.Empty or TemplateStatuses.Draft or TemplateStatuses.SavedDraft;
}
