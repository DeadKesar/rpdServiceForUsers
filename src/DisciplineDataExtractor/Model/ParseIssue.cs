namespace DisciplineDataExtractor;

public enum IssueSeverity
{
    /// <summary>Данные разобраны, но требуют внимания (например, сопоставление по словам).</summary>
    Warning,

    /// <summary>Часть данных потеряна или файл не соответствует ожидаемому формату.</summary>
    Error
}

/// <summary>
/// Замечание разбора с привязкой к месту. Для .xlsx <see cref="Sheet"/> — лист, для .docx — «Таблица N».
/// <see cref="Row"/> — номер строки листа или таблицы (с 1); null — замечание ко всему файлу или листу.
/// </summary>
public sealed record ParseIssue(IssueSeverity Severity, string File, string? Sheet, int? Row, string Message)
{
    public override string ToString() =>
        $"{(Severity == IssueSeverity.Error ? "Ошибка" : "Предупреждение")}: {File}"
        + (Sheet is null ? "" : $", {Sheet}")
        + (Row is null ? "" : $", строка {Row}")
        + $" — {Message}";
}
