using RpdFos.Core.DocumentTypes;

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

    /// <summary>Новый шаблон для версии учебного плана: снимок структуры типа, статус «Пустой».</summary>
    public static DocumentTemplate Create(DocumentTypeDefinition type, string name, Guid curriculumPlanId, Guid createdBy) => new()
    {
        Type = type.Code,
        Name = name,
        CurriculumPlanId = curriculumPlanId,
        CreatedBy = createdBy,
        Schema = type.SchemaJson,
        SignaturesConfig = type.SignaturesJson
    };

    /// <summary>Загружен DOCX-шаблон оформления: его сопоставление закладок, статус «Черновик».</summary>
    public void AttachPrintTemplate(Guid fileId, string printMapping)
    {
        Require(TemplateTransitions.CanAttachPrintTemplate(Status), TemplateTransitions.AttachAction);
        PrintTemplateFileId = fileId;
        PrintMapping = printMapping;
        Status = TemplateStatuses.Draft;
    }

    public void Save()
    {
        Require(TemplateTransitions.CanSave(Status), TemplateTransitions.SaveAction);
        Status = TemplateStatuses.SavedDraft;
    }

    /// <summary>
    /// Публикация — только после успешной проверки (<c>TemplateCheck</c>). Прежнюю опубликованную редакцию
    /// архивирует <c>TemplateLifecycle</c>.
    /// </summary>
    public void Publish(DateTimeOffset now)
    {
        Require(TemplateTransitions.CanPublish(Status), TemplateTransitions.PublishAction);
        Status = TemplateStatuses.Published;
        PublishedAt = now;
    }

    public void Archive()
    {
        Require(TemplateTransitions.CanArchive(Status), TemplateTransitions.ArchiveAction);
        Status = TemplateStatuses.Archived;
    }

    /// <summary>Мягкое удаление: статус и флаг — вместе.</summary>
    public void Delete(DateTimeOffset now)
    {
        Require(TemplateTransitions.CanDelete(Status), TemplateTransitions.DeleteAction);
        Status = TemplateStatuses.Deleted;
        IsDeleted = true;
        DeletedAt = now;
    }

    /// <summary>
    /// Новая редакция опубликованного шаблона: та же линия редакций, копия структуры, сопоставления и листа
    /// согласования, тот же файл оформления, статус «Черновик». Номер — следующий в линии, с учётом удалённых.
    /// </summary>
    public DocumentTemplate CreateRevision(int revision, Guid createdBy)
    {
        Require(TemplateTransitions.CanCreateRevision(Status), TemplateTransitions.RevisionAction);
        return new DocumentTemplate
        {
            Type = Type,
            Name = Name,
            LineageId = LineageId,
            Revision = revision,
            CurriculumPlanId = CurriculumPlanId,
            CreatedBy = createdBy,
            Schema = Schema,
            SignaturesConfig = SignaturesConfig,
            PrintTemplateFileId = PrintTemplateFileId,
            PrintMapping = PrintMapping,
            Status = TemplateStatuses.Draft
        };
    }

    // Эндпоинты проверяют переход заранее и отвечают 409; здесь — защита от ошибки в коде.
    private void Require(bool allowed, string action)
    {
        if (!allowed) throw new InvalidOperationException(TemplateTransitions.Refusal(Status, action));
    }
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

    public static string Title(string status) => status switch
    {
        Empty => "Пустой",
        Draft => "Черновик",
        SavedDraft => "Сохранённый черновик",
        Published => "Опубликован",
        Archived => "Архивный",
        Deleted => "Удалённый",
        _ => status
    };
}

/// <summary>Разрешённые переходы статусов шаблона — в одном месте, а не в эндпоинтах.</summary>
public static class TemplateTransitions
{
    // Действия — для сообщения «Шаблон в статусе … нельзя …».
    public const string AttachAction = "загрузить шаблон оформления", SaveAction = "сохранить",
        PublishAction = "опубликовать", ArchiveAction = "архивировать", DeleteAction = "удалить",
        RevisionAction = "создать новую редакцию";

    // Шаблон оформления можно заменить, пока шаблон не опубликован; опубликованный меняется новой редакцией.
    public static bool CanAttachPrintTemplate(string status) =>
        status is TemplateStatuses.Empty or TemplateStatuses.Draft or TemplateStatuses.SavedDraft;

    public static bool CanSave(string status) => status is TemplateStatuses.Draft;

    // Из «Пустого» — нельзя: без шаблона оформления проверка перед публикацией не пройдёт (FR-TPL-11, FR-TPL-13).
    public static bool CanPublish(string status) => status is TemplateStatuses.Draft or TemplateStatuses.SavedDraft;

    public static bool CanArchive(string status) => status is TemplateStatuses.Published;

    public static bool CanDelete(string status) =>
        status is TemplateStatuses.Empty or TemplateStatuses.Draft or TemplateStatuses.SavedDraft;

    public static bool CanCreateRevision(string status) => status is TemplateStatuses.Published;

    public static string Refusal(string status, string action) =>
        $"Шаблон в статусе «{TemplateStatuses.Title(status)}» нельзя {action}.";
}
