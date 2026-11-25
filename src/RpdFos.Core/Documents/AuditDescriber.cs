using System.Text.Json.Nodes;
using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Человеческое описание записи аудита: название события и подробности. Общее для истории документа (этап 8, A10) и журнала
/// аудита администратора (A11) — иначе одно событие называлось бы в двух местах по-разному.
/// </summary>
public static class AuditDescriber
{
    // Названия полей записи справочника в снимках «было / стало» (имена — snake_case, как в payload).
    private static readonly (string Key, string Label)[] EmployeeFields =
    [
        ("short_name", "Фамилия И. О."), ("name_for_document", "Подпись в документах"), ("full_name", "ФИО"), ("email", "Email"),
        ("position", "Должность"), ("academic_degree", "Учёная степень"), ("official_designation", "Офиц. название"),
        ("signatory_role", "Категория подписанта"), ("department_id", "Кафедра"), ("institute_id", "Институт"),
        ("direction_id", "Направление"), ("is_active", "Действует")
    ];

    /// <summary>Все события, которые пишет система, — для списка типов в фильтре журнала.</summary>
    public static readonly string[] KnownEvents =
    [
        AuditEvents.PlanFieldCorrected, AuditEvents.DocumentDeleted, AuditEvents.ReturnedForCorrection, AuditEvents.StatusForced,
        AuditEvents.DocumentArchived, AuditEvents.DocumentTransferred, AuditEvents.DocumentBasedOn, AuditEvents.DocumentOutdated,
        AuditEvents.RpdSnapshotRefreshed, AuditEvents.BankRenumbered, AuditEvents.ApprovalStarted, AuditEvents.SignatureApproved,
        AuditEvents.SignatureRejected, AuditEvents.SignatoryAssigned, AuditEvents.DisciplineAssigned, AuditEvents.DisciplineUnassigned,
        AuditEvents.EmployeeCreated, AuditEvents.EmployeeUpdated, AuditEvents.EmployeeDeactivated, AuditEvents.EmployeeReactivated,
        AuditEvents.EmployeeReplaced, AuditEvents.DepartmentUpdated, AuditEvents.MatrixRowLinked, AuditEvents.MatrixRowUnlinked
    ];

    public static string Title(string eventType) => eventType switch
    {
        AuditEvents.PlanFieldCorrected => "Исправлено поле из учебного плана",
        AuditEvents.DocumentDeleted => "Черновик удалён",
        AuditEvents.ReturnedForCorrection => "Возвращён на исправление",
        AuditEvents.StatusForced => "Статус сменён принудительно",
        AuditEvents.DocumentArchived => "Отправлен в архив",
        AuditEvents.DocumentTransferred => "Передан другому автору",
        AuditEvents.DocumentBasedOn => "Создан на основе другого документа",
        AuditEvents.DocumentOutdated => "Помечен «устарел»",
        AuditEvents.RpdSnapshotRefreshed => "Снимок РПД обновлён",
        AuditEvents.BankRenumbered => "Перенумерована группа банка заданий",
        AuditEvents.ApprovalStarted => "Начат круг согласования",
        AuditEvents.SignatureApproved => "Согласовано",
        AuditEvents.SignatureRejected => "Согласующий вернул документ",
        AuditEvents.SignatoryAssigned => "Назначен согласующий",
        AuditEvents.DisciplineAssigned => "Дисциплина закреплена за сотрудником",
        AuditEvents.DisciplineUnassigned => "Закрепление дисциплины снято",
        AuditEvents.EmployeeCreated => "Создана запись справочника сотрудников",
        AuditEvents.EmployeeUpdated => "Изменена запись справочника сотрудников",
        AuditEvents.EmployeeDeactivated => "Запись справочника отключена",
        AuditEvents.EmployeeReactivated => "Запись справочника возвращена",
        AuditEvents.EmployeeReplaced => "Запись справочника заменена преемником",
        AuditEvents.DepartmentUpdated => "Исправлено название кафедры",
        AuditEvents.MatrixRowLinked => "Строка матрицы привязана к дисциплине",
        AuditEvents.MatrixRowUnlinked => "Привязка строки матрицы отменена",
        _ => eventType
    };

    /// <summary>Записи справочника, названия которых нужны описанию: из содержимого записей аудита.</summary>
    public static IEnumerable<Guid> PeopleIn(JsonNode? p)
    {
        if (p is null) yield break;
        foreach (var key in new[] { "from", "to", "before", "after", "employee" })
            if (p[key] is JsonValue v && Guid.TryParse(v.ToString(), out var id)) yield return id;
        if (p["seats"] is JsonArray seats)
            foreach (var s in seats)
                if (Guid.TryParse(s?["signatory"]?.ToString(), out var id)) yield return id;
    }

    public static HistoryEntry Describe(AuditLogEntry a, JsonNode? p, Func<Guid?, string?> nameOf)
    {
        string? Text(string key) => p?[key]?.ToString();
        string Person(string key) => Guid.TryParse(Text(key), out var id) ? nameOf(id) ?? "не найден в справочнике" : "не назначен";
        string Seat(string? role) => role is null ? "место" : SignatoryRoles.Title(role);

        HistoryEntry Entry(string? title = null, string? from = null, string? to = null, string? comment = null, params string[] details) =>
            new(a.OccurredAt, "event", a.EventType, title ?? Title(a.EventType), a.UserId, nameOf(a.UserId),
                from is null ? null : DocumentStatuses.Title(from), to is null ? null : DocumentStatuses.Title(to), comment, details);

        return a.EventType switch
        {
            AuditEvents.ReturnedForCorrection => Entry(from: Text("from"), to: Text("to"), comment: Text("comment")),
            AuditEvents.StatusForced => Entry(from: Text("from"), to: Text("to"), comment: Text("comment"),
                details: p?["approval_interrupted"]?.GetValue<bool>() == true ? ["Круг согласования прерван"] : []),
            AuditEvents.DocumentArchived => Entry(from: Text("from"), to: Text("to")),
            AuditEvents.DocumentDeleted => Entry(details: $"Статус перед удалением: {StatusOrDash(Text("status"))}"),
            AuditEvents.DocumentTransferred => Entry(comment: Text("reason"),
                details: $"Прежний автор: {Person("from")}; новый автор: {Person("to")}"),
            AuditEvents.PlanFieldCorrected => Entry(details: $"{Text("path")}: было «{Text("before")}», стало «{Text("after")}»"),
            AuditEvents.ApprovalStarted => Entry(details: Seats(p, nameOf)),
            AuditEvents.SignatureApproved => Entry(details: $"Место: {Seat(Text("role"))}"),
            // Причина отказа — в переходе «Возврат с согласования»; в истории документа она не повторяется.
            AuditEvents.SignatureRejected => Entry(details: $"Место: {Seat(Text("role"))}"),
            AuditEvents.SignatoryAssigned => Entry(
                details: $"Место: {Seat(Text("role"))}; было: {Person("before")}; стало: {Person("after")}"),
            AuditEvents.DocumentBasedOn => Entry(details: $"Основа: «{Text("title")}»"),
            AuditEvents.DocumentOutdated => Entry(details: "Согласован документ на его основе для той же дисциплины"),
            AuditEvents.RpdSnapshotRefreshed => Entry(details: $"Версия РПД: {Text("from_version")} → {Text("to_version")}"),
            AuditEvents.BankRenumbered => Entry(details: $"Группа: {Text("group")}"),
            AuditEvents.DisciplineAssigned or AuditEvents.DisciplineUnassigned => Entry(
                details: p?["document"] is not null
                    ? [$"Сотрудник: {Person("employee")}", "Закрепление при передаче документа"]
                    : [$"Сотрудник: {Person("employee")}"]),
            AuditEvents.EmployeeCreated => Entry(details: EmployeeDetails(p, created: true)),
            AuditEvents.EmployeeUpdated or AuditEvents.EmployeeDeactivated or AuditEvents.EmployeeReactivated
                or AuditEvents.EmployeeReplaced => Entry(details: EmployeeDetails(p, created: false)),
            AuditEvents.DepartmentUpdated => Entry(details: DepartmentDetails(p)),
            AuditEvents.MatrixRowLinked => Entry(
                p?["status"]?.ToString() == MatrixRowStatuses.Ignored ? "Строка матрицы отмечена «Не учитывать»" : null,
                details: [$"Строка: «{Text("row")}»", $"Добавлено связей с компетенциями: {CountOf(p, "added")}"]),
            AuditEvents.MatrixRowUnlinked => Entry(
                details: [$"Строка: «{Text("row")}»", $"Снято связей с компетенциями: {CountOf(p, "removed")}"]),
            _ => Entry()
        };
    }

    private static string StatusOrDash(string? status) => status is null ? "—" : DocumentStatuses.Title(status);

    private static int CountOf(JsonNode? p, string key) => p?[key] is JsonArray a ? a.Count : 0;

    // Запись справочника: при создании — кто создан, иначе — что изменилось. Подразделения показываются словом «изменено»: в
    // содержимом только их коды, а названия — отдельные запросы, ради строки журнала не нужные.
    private static string[] EmployeeDetails(JsonNode? p, bool created)
    {
        var before = p?["before"] as JsonObject;
        var after = p?["after"] as JsonObject;
        if (after is null) return [];
        if (created) return [$"Запись: «{after["short_name"]}»"];

        var lines = new List<string> { $"Запись: «{after["short_name"]}»" };
        foreach (var (key, label) in EmployeeFields)
        {
            var was = before?[key]?.ToString();
            var now = after[key]?.ToString();
            if (was == now) continue;
            lines.Add(key.EndsWith("_id", StringComparison.Ordinal) ? $"{label}: изменено" : $"{label}: было «{Value(key, was)}», стало «{Value(key, now)}»");
        }
        return [.. lines];
    }

    // Кафедра: только то, что изменилось, — название и краткое название.
    private static string[] DepartmentDetails(JsonNode? p)
    {
        var lines = new List<string>();
        foreach (var (key, label) in new[] { ("name", "Название"), ("short_name", "Краткое название") })
        {
            var was = p?["before"]?[key]?.ToString();
            var now = p?["after"]?[key]?.ToString();
            if (was != now) lines.Add($"{label}: было «{was ?? "—"}», стало «{now ?? "—"}»");
        }
        return [.. lines];
    }

    private static string Value(string key, string? value) => key switch
    {
        _ when value is null => "—",
        "is_active" => value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "да" : "нет",
        "signatory_role" => SignatoryRoles.Title(value),
        _ => value
    };

    private static string[] Seats(JsonNode? p, Func<Guid?, string?> nameOf) =>
        p?["seats"] is JsonArray seats
            ? seats.OfType<JsonObject>().Select(s =>
            {
                var role = s["role"]?.ToString();
                var who = Guid.TryParse(s["signatory"]?.ToString(), out var id) ? nameOf(id) ?? "не найден в справочнике" : "не найден";
                return $"{(role is null ? "место" : SignatoryRoles.Title(role))} — {who}";
            }).ToArray()
            : [];
}
