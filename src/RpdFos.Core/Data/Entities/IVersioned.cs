namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Сущность с оптимистичной блокировкой (NFR-REL-04, раздел 2.6 ВКР).
/// Номер версии и время изменения ведёт перехватчик сохранения, а не код эндпоинтов.
/// </summary>
public interface IVersioned
{
    int Version { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
