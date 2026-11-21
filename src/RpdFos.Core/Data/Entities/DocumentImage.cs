namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Связь «документ — рисунок» (таблица document_images — дополнение модели данных, этап 7, B4а). Рисунок — запись
/// <c>uploaded_files</c> с типом <see cref="UploadedFileTypes.Image"/>; один файл может быть связан с несколькими документами
/// («Создать на основе», рисунок из чужого документа, который автор читает): файл не копируется. Рисунок читает тот, кто читает
/// хотя бы один связанный документ. Строка не правится: связь появляется при загрузке, вставке или переносе в новый документ.
/// </summary>
public class DocumentImage
{
    public Guid DocumentId { get; set; }

    public Guid FileId { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
}
