namespace RpdFos.Core.Data.Entities;

/// <summary>Институт (таблица institutes, контекст Reference, раздел 2.4 ВКР).</summary>
public class Institute
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Код — сокращение из файла должностей («ИИТ»); если его нет — название.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string? ShortName { get; set; }
}

/// <summary>Кафедра (таблица departments). Название согласовано с учебным планом — по нему и сопоставляется.</summary>
public class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? InstituteId { get; set; }
    public Institute? Institute { get; set; }

    /// <summary>Номер кафедры в учебном плане (лист «Кафедры»), на него ссылаются дисциплины.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
}
