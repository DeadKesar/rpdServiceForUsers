namespace RpdFos.Web.Models;

/// <summary>
/// Текст подтверждения «Принять» (этап 8, A1; UC-07). Приём запускает согласование, если подписанты всех электронных мест
/// найдены; иначе документ станет «Принят», а согласование начнётся после того, как методист назначит согласующих, — об этом
/// и говорит подтверждение: прежнее «отправляете на согласование» обещало то, чего приём не сделает.
/// </summary>
public static class AcceptConfirmation
{
    /// <param name="unassigned">Места, подписанты которых не найдены (<see cref="DocumentDetails.AcceptUnassigned"/>).</param>
    /// <param name="crossWarnings">Предупреждения сквозной проверки РПД ↔ ФОС (этап 7, B8): приём они не блокируют.</param>
    public static string Question(IReadOnlyList<string>? unassigned, int crossWarnings)
    {
        var cross = crossWarnings > 0 ? $"Сквозная проверка РПД ↔ ФОС: расхождений {crossWarnings}. Приём они не блокируют. " : "";
        return unassigned is { Count: > 0 }
            ? $"{cross}Подписанты не найдены у мест: {string.Join("; ", unassigned)}. " +
              "Документ станет «Принят», согласование начнётся после назначения согласующих. Уверены?"
            : $"{cross}Вы отправляете документ на согласование. Уверены?";
    }
}
