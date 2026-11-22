using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.DocumentTypes;
using RpdFos.Core.Templates;

namespace RpdFos.Core.Documents;

/// <summary>Итог переноса из основы: содержимое нового документа и список того, что в него не попало.</summary>
public sealed record BasingResult(JsonObject Payload, IReadOnlyList<string> NotTransferred);

/// <summary>
/// Перенос содержимого основы в новый документ («Создать на основе», этап 7, A11; FR-DOC-07, решение 4). Цель — та же
/// дисциплина или дисциплина другого плана, а схемы шаблонов основы и цели могут различаться (другая редакция, другой шаблон),
/// поэтому переносится не документ целиком, а ввод автора по совпадающим полям схем.
/// <list type="bullet">
/// <item>Поля из плана и справочников (<c>x-source</c>) берутся заново: новый документ собран для цели как с нуля (титул,
/// место в структуре, часы, виды аттестации, компетенции из матрицы, автор среди составителей).</item>
/// <item>Компетенции — из матрицы цели; выбранные индикаторы остаются по кодам, формулировка — из справочника. Индикатор без
/// пары (нет компетенции в матрице цели или такого индикатора у неё) назван в списке.</item>
/// <item>Не переносятся: разделы, скрытые для цели (<c>x-visible-if</c>), составители (в новом документе — только автор) и
/// поля, обязательные для статуса (<c>x-required-for-status</c>, протокол кафедры): они относятся к согласованию самого
/// документа. Постоянные пункты вуза (<c>x-template-default</c>) берутся из нового шаблона, если автор их не менял.
/// Ссылки на дисциплины плана переносятся, если эта дисциплина есть в плане цели.</item>
/// <item>Поле основы, которого нет в схеме нового шаблона или тип которого изменился, тоже в списке.</item>
    /// <item>Разделы только для чтения (<c>x-readonly</c>, снимок РПД в ФОС) ведёт система: в новом документе они собраны для цели
    /// и из основы не переносятся (B10).</item>
/// </list>
/// Правило без базы: проверяется на схемах и содержимом, поэтому тесты не поднимают хост.
/// </summary>
public static class DocumentBasing
{
    private const string CompetenceSource = "db.competence_matrix.by_discipline", DeveloperSource = "db.user.current",
        ReadOnlyKeyword = "x-readonly";

    /// <param name="fresh">
    /// Содержимое нового документа, собранное для цели как при создании с нуля: поля из плана и справочников, автор среди
    /// составителей, значения по умолчанию шаблона. Перенос дополняет его в том же объекте.
    /// </param>
    /// <param name="baseDefaults">
    /// Значения по умолчанию шаблона основы: по ним видно, менял ли автор постоянные пункты вуза.
    /// </param>
    /// <param name="hidden">Места, не предусмотренные учебным планом цели (<see cref="DocumentMetadata.Hidden"/>).</param>
    public static BasingResult Transfer(
        JsonObject fresh, JsonElement targetSchema, JsonElement baseSchema, JsonNode? baseDefaults, JsonObject basePayload,
        DisciplineFacts facts, Guid authorId, IReadOnlyList<string> hidden, Func<string, JsonElement?> resolve)
    {
        var run = new Run(targetSchema, baseSchema, baseDefaults, facts, authorId, hidden, resolve);
        var targetRoot = targetSchema;
        if (SchemaInspector.Deref(targetSchema, ref targetRoot, resolve) is { } target)
            run.Forward(target, targetRoot, "", [], fresh, basePayload, false, 0);
        var baseRoot = baseSchema;
        if (SchemaInspector.Deref(baseSchema, ref baseRoot, resolve) is { } source)
            run.Backward(source, baseRoot, "", [], basePayload, 0);
        return new(fresh, run.Notes);
    }

    private sealed class Run(
        JsonElement targetSchema, JsonElement baseSchema, JsonNode? baseDefaults, DisciplineFacts facts, Guid authorId,
        IReadOnlyList<string> hidden, Func<string, JsonElement?> resolve)
    {
        public List<string> Notes { get; } = [];

        /// <summary>По схеме цели: что из основы можно положить на каждое её поле.</summary>
        public void Forward(
            JsonElement node, JsonElement root, string path, IReadOnlyList<string> titles, JsonObject target, JsonObject? from,
            bool inDefault, int depth)
        {
            if (depth > 16 || !node.TryGetProperty("properties", out var properties)) return;
            foreach (var p in properties.EnumerateObject())
            {
                var childRoot = root;
                if (SchemaInspector.Deref(p.Value, ref childRoot, resolve) is not { ValueKind: JsonValueKind.Object } child) continue;
                var pointer = $"{path}/{p.Name}";
                var named = titles.Append(Text(child, "title") ?? Text(p.Value, "title") ?? p.Name).ToList();
                var value = from?[p.Name];
                var isDefault = inDefault || Declares(p.Value, child, TemplateDefaults.Marker, mustBeTrue: true);

                if (IsHidden(pointer))
                {
                    // Стандартная ссылка вуза в скрытом месте — не ввод автора: сообщать о ней нечего.
                    if (Has(value) && !(isDefault && Unchanged(pointer, value)))
                        Notes.Add($"{Join(named)}: не предусмотрено учебным планом дисциплины — не перенесено");
                    continue;
                }

                // Раздел только для чтения ведёт система, а не автор (снимок РПД в ФОС): в новом документе он собран для цели.
                if (Declares(p.Value, child, ReadOnlyKeyword, mustBeTrue: true)) continue;

                // Поле из плана или справочника — заново. Особые случаи: компетенции и составители.
                if ((Text(p.Value, "x-source") ?? Text(child, "x-source")) is { } source)
                {
                    if (source == CompetenceSource) Competences(value, target, p.Name, named);
                    else if (source == DeveloperSource) Developers(value, named);
                    continue;
                }
                if (Declares(p.Value, child, "x-required-for-status"))
                {
                    if (Has(value)) Notes.Add($"{Join(named)}: относится к согласованию самого документа — укажите заново");
                    continue;
                }

                if (child.TryGetProperty("properties", out _))
                {
                    var existing = target[p.Name] as JsonObject;
                    var inner = existing ?? new JsonObject();
                    Forward(child, childRoot, pointer, named, inner, value as JsonObject, isDefault, depth + 1);
                    if (existing is null && inner.Count > 0) target[p.Name] = inner;
                    if (value is not null and not JsonObject && Has(value))
                        Notes.Add($"{Join(named)}: тип поля в новом шаблоне другой — не перенесено");
                    continue;
                }
                Leaf(child, pointer, named, p.Name, target, value, isDefault);
            }
        }

        /// <summary>По схеме основы: ввод автора, которому в новом шаблоне нет места.</summary>
        public void Backward(
            JsonElement node, JsonElement root, string path, IReadOnlyList<string> titles, JsonObject? from, int depth)
        {
            if (from is null || depth > 16 || !node.TryGetProperty("properties", out var properties)) return;
            foreach (var p in properties.EnumerateObject())
            {
                var childRoot = root;
                if (SchemaInspector.Deref(p.Value, ref childRoot, resolve) is not { ValueKind: JsonValueKind.Object } child) continue;
                var value = from[p.Name];
                // Поля из плана основа не вводила; составители и протокол названы отдельно (Forward).
                if (!Has(value) || Text(p.Value, "x-source") is not null || Text(child, "x-source") is not null ||
                    Declares(p.Value, child, "x-required-for-status") || Declares(p.Value, child, ReadOnlyKeyword, mustBeTrue: true))
                    continue;

                var pointer = $"{path}/{p.Name}";
                var named = titles.Append(Text(child, "title") ?? Text(p.Value, "title") ?? p.Name).ToList();
                if (SchemaInspector.SubschemaAt(targetSchema, pointer, resolve) is null)
                    Notes.Add($"{Join(named)}: поля нет в схеме нового шаблона — не перенесено");
                else
                    Backward(child, childRoot, pointer, named, value as JsonObject, depth + 1);
            }
        }

        private void Leaf(
            JsonElement child, string pointer, IReadOnlyList<string> titles, string name, JsonObject target, JsonNode? value,
            bool isDefault)
        {
            if (!Has(value)) return;
            // У основы такого поля нет — переносить нечего (если оно есть в содержимом, значит, оно осталось от старой схемы).
            if (SchemaInspector.SubschemaAt(baseSchema, pointer, resolve) is not { } was) return;
            if (Kind(was) != Kind(child))
            {
                Notes.Add($"{Join(titles)}: тип поля в новом шаблоне другой — не перенесено");
                return;
            }
            // Постоянные пункты вуза — значения нового шаблона (они уже в новом содержимом), если автор их не менял.
            if (isDefault && Unchanged(pointer, value)) return;

            var carried = value!.DeepClone();
            if (child.TryGetProperty("x-plan-disciplines", out var relation) && carried is JsonArray items)
                carried = PlanDisciplines(items, relation.GetString(), titles);
            if (Has(carried)) target[name] = carried;
        }

        // Выбранные индикаторы остаются по кодам; компетенции и формулировки индикаторов — из матрицы цели.
        private void Competences(JsonNode? value, JsonObject target, string name, IReadOnlyList<string> titles)
        {
            if (value is not JsonArray chosen) return;
            var fresh = target[name] as JsonArray ?? [];
            foreach (var competence in chosen.OfType<JsonObject>())
            {
                if (competence["indicators"] is not JsonArray indicators || indicators.Count == 0) continue;
                var code = Str(competence["code"]) ?? "";
                var inMatrix = facts.Competences.FirstOrDefault(c => c.Code == code);
                var slot = fresh.OfType<JsonObject>().FirstOrDefault(c => Str(c["code"]) == code)?["indicators"] as JsonArray;
                foreach (var indicator in indicators.OfType<JsonObject>())
                {
                    var indicatorCode = Str(indicator["code"]) ?? "";
                    var found = inMatrix?.Indicators.FirstOrDefault(i => i.Code == indicatorCode);
                    if (found is null || slot is null)
                    {
                        Notes.Add($"{Join(titles)}: индикатор {indicatorCode} не перенесён — " + (inMatrix is null
                            ? $"компетенции {code} нет в матрице дисциплины"
                            : $"у компетенции {code} в матрице дисциплины нет такого индикатора"));
                        continue;
                    }
                    var carried = (JsonObject)indicator.DeepClone();
                    carried["text"] = found.Text;
                    slot.Add(carried);
                }
            }
        }

        // Составители в новом документе — только автор: остальных основа не называет, их добавляет автор.
        private void Developers(JsonNode? value, IReadOnlyList<string> titles)
        {
            List<JsonObject> listed = value switch { JsonArray a => a.OfType<JsonObject>().ToList(), JsonObject o => [o], _ => [] };
            var others = listed.Count(d => Str(d["user_id"]) != authorId.ToString());
            if (others > 0)
                Notes.Add($"{Join(titles)}: в новом документе указан только автор, остальные составители основы не перенесены ({others})");
        }

        // Ссылки на дисциплины плана — только на те, что есть в плане нового документа: у другого плана свои строки.
        private JsonArray PlanDisciplines(JsonArray items, string? relation, IReadOnlyList<string> titles)
        {
            var candidates = (relation == "later" ? facts.Later : facts.Earlier).Select(c => c.Id).ToHashSet();
            var kept = new JsonArray();
            foreach (var item in items)
            {
                if (item is JsonObject o && Guid.TryParse(Str(o["discipline_id"]), out var id) && candidates.Contains(id))
                    kept.Add(o.DeepClone());
                else
                    Notes.Add($"{Join(titles)}: «{Label(item)}» — нет среди подходящих дисциплин плана нового документа");
            }
            return kept;
        }

        private bool IsHidden(string pointer) =>
            hidden.Any(h => pointer == h || pointer.StartsWith(h + "/", StringComparison.Ordinal));

        // Значение равно умолчанию шаблона основы: автор постоянный пункт не менял.
        private bool Unchanged(string pointer, JsonNode? value) => JsonNode.DeepEquals(At(baseDefaults, pointer), value);
    }

    private static bool Has(JsonNode? value) => value switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Any(p => Has(p.Value)),
        JsonValue v => !(v.TryGetValue(out string? s) && string.IsNullOrWhiteSpace(s)),
        _ => true
    };

    // Расширение на самом поле или на том, куда ведёт $ref.
    private static bool Declares(JsonElement field, JsonElement resolved, string keyword, bool mustBeTrue = false) =>
        Marked(field, keyword, mustBeTrue) || Marked(resolved, keyword, mustBeTrue);

    private static bool Marked(JsonElement node, string keyword, bool mustBeTrue) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(keyword, out var v) &&
        (!mustBeTrue || v.ValueKind == JsonValueKind.True);

    // Тип поля для сравнения схем основы и цели: у списка — ещё и тип элемента (или ссылка на него).
    private static string Kind(JsonElement field)
    {
        var type = Text(field, "type") ?? "";
        if (type != "array" || !field.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object) return type;
        return $"array<{Text(items, "type") ?? Text(items, "$ref") ?? ""}>";
    }

    private static JsonNode? At(JsonNode? node, string pointer)
    {
        foreach (var segment in pointer.TrimStart('/').Split('/'))
            node = node is JsonObject o && o.TryGetPropertyValue(segment, out var next) ? next : null;
        return node;
    }

    private static string Join(IReadOnlyList<string> titles) => string.Join(" → ", titles);

    private static string Label(JsonNode? item) =>
        item is JsonObject o ? $"{Str(o["code"])} {Str(o["name"])}".Trim() : Str(item) ?? "";

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
