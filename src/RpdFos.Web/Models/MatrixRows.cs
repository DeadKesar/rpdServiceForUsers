namespace RpdFos.Web.Models;

// Контракт API строк матрицы без дисциплины плана (Features/Imports) на стороне клиента (этапы 5.2 и 7.1).

/// <summary>
/// Дисциплина плана в выборе строки: кандидат неоднозначной строки, уже привязанная другой строкой блока или без связей
/// с матрицей — выше прочих.
/// </summary>
public sealed record MatrixRowOptionItem(Guid Id, string Code, string Name, bool Candidate, bool LinkedHere, bool WithoutMatrix);

/// <summary>
/// Строка блока: одна строка матрицы или группа одинаковых (этап 7.1). <paramref name="Id"/> — первая строка группы, по ней
/// привязывают и отменяют всю группу; <paramref name="Places"/> — места в файле («Таблица 1, строка 49»).
/// </summary>
public sealed record MatrixRowItem(
    Guid Id, string Name, List<string> Places, string Semesters, List<string> Codes, string Status, Guid? DisciplineId,
    string? Discipline, string? ResolvedBy, DateTimeOffset? ResolvedAt, List<MatrixRowOptionItem> Options);

/// <summary>
/// Что выбрано в списке «Дисциплина плана» у каждой строки блока. Выбор держится за дисциплиной — её идентификатором, а не
/// положением в списке: когда другие строки привязывают или отменяют, у остальных порядок вариантов меняется (дисциплина
/// получила связи — ушла из «без связей с матрицей»), и показано должно быть то, что будет привязано (этап 7.1).
/// </summary>
public sealed class MatrixRowChoices
{
    /// <summary>Значение списка для «Не учитывать».</summary>
    public const string Ignore = "ignore";

    private readonly Dictionary<Guid, string> choice = new();

    /// <summary>Выбор строки: пусто, <see cref="Ignore"/> или идентификатор дисциплины.</summary>
    public string Get(Guid rowId) => choice.GetValueOrDefault(rowId, "");

    public void Set(Guid rowId, string? value) => choice[rowId] = value ?? "";

    /// <summary>Строки перечитаны: выбор остаётся там, где его можно применить; у решённых строк и без такой дисциплины среди вариантов — сброшен.</summary>
    public void Reconcile(IEnumerable<MatrixRowItem> rows)
    {
        var current = rows.ToDictionary(r => r.Id);
        foreach (var id in choice.Keys.Where(id => !current.ContainsKey(id)).ToList()) choice.Remove(id);
        foreach (var row in current.Values)
        {
            var value = Get(row.Id);
            var valid = row.Status == "pending" &&
                        (value == Ignore || (Guid.TryParse(value, out var disciplineId) && row.Options.Any(o => o.Id == disciplineId)));
            choice[row.Id] = valid ? value : "";
        }
    }

    /// <summary>Дисциплина выбора: идентификатор, null — «Не учитывать»; false — ничего не выбрано.</summary>
    public bool TryGetTarget(Guid rowId, out Guid? disciplineId)
    {
        disciplineId = null;
        var value = Get(rowId);
        if (value == Ignore) return true;
        if (!Guid.TryParse(value, out var id)) return false;
        disciplineId = id;
        return true;
    }
}
