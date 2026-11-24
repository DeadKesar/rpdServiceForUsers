using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

public enum SectionUpdateProblem { UnknownSection, HiddenSection, ReadOnlyChanged, Invalid, ReturnNotConfirmed }

/// <summary>
/// Итог изменения раздела: отказ с ошибками по полям или новое состояние раздела — и всех разделов: правка одного
/// меняет заполненность других (тематический план сверяется с трудоёмкостью), исправление трудоёмкости — состав
/// разделов и скрытые места (этап 5.2).
/// </summary>
public sealed record SectionUpdateResult(SectionUpdateProblem? Problem, IReadOnlyList<DocumentValidationError> Errors, SectionState? Section,
    IReadOnlyList<SectionState>? Sections = null, IReadOnlyList<string>? HiddenPaths = null, JsonElement? Payload = null,
    IReadOnlyList<CommentMove>? CommentMoves = null);

/// <summary>Где окажется замечание после сохранения раздела (этап 8, A14): новый путь и признак «запись удалена».</summary>
public sealed record CommentMove(Guid Id, string Path, bool IsRecordDeleted);

/// <summary>
/// Изменение и сохранение документа. Раздел проверяется в режиме черновика, форматированный текст санитизируется,
/// поля из учебного плана не меняются; нормализованные таблицы, снимок версии и история статусов попадают в то же
/// сохранение, что и содержимое. Сохраняет вызывающий — с версией клиента (оптимистичная блокировка).
/// </summary>
public sealed class DocumentEditing(
    AppDbContext db, DocumentTypeRegistry registry, IDocumentValidator validator, RichTextSanitizer sanitizer, DocumentImages images,
    ValidationRuleSet rules, DocumentHistory history, DocumentNotifications notifications, TimeProvider clock)
{
    public const string ReadOnlyChanged = "Поле из учебного плана — изменять нельзя";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string PlanOnlyChanged = "В этом статусе исправляются только поля из учебного плана";

    public const string ReturnedAfterPlanCorrection = "возвращён после исправления данных плана";

    /// <param name="scope">
    /// Что можно менять (<see cref="DocumentTransitions.EditScope"/>): поля из плана — только методисту и администратору;
    /// каждое их изменение — запись в журнале аудита (кто, когда, было, стало).
    /// </param>
    /// <param name="confirmReturn">
    /// Исправление принятого документа, после которого полная проверка находит ошибки, возвращает его автору — только
    /// с подтверждением исправляющего; без него — <see cref="SectionUpdateProblem.ReturnNotConfirmed"/> с причинами.
    /// </param>
    /// <param name="editorRoles">
    /// Роли правящего из запроса: по ним определяется, читает ли он документ, из которого вставлен рисунок (этап 7, B4а); без них —
    /// роли действующей учётной записи из базы.
    /// </param>
    /// <param name="checkOnly">
    /// Проверка без сохранения (этап 5.2, B4.1): тот же путь до проверки черновика, затем — состояние разделов по
    /// присланному содержимому. Документ, версия, снимки, история и журнал аудита не меняются; ошибки черновика —
    /// в ответе вместе с разделами.
    /// </param>
    public async Task<SectionUpdateResult> UpdateSectionAsync(
        Document document, string section, JsonElement value, Guid editorId, CancellationToken ct = default,
        DocumentEditScope scope = DocumentEditScope.Content, bool confirmReturn = false, bool checkOnly = false,
        IReadOnlyList<string>? editorRoles = null)
    {
        var schemaJson = await SchemaAsync(document, ct);
        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var root = schema;
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(section, out var raw) ||
            SchemaInspector.Deref(raw, ref root, registry.FindSchema) is not { ValueKind: JsonValueKind.Object } sectionSchema)
            return new(SectionUpdateProblem.UnknownSection, [], null);

        var facts = (await DisciplineFacts.LoadForAsync(db, document, ct))!;
        if (!ConditionalSections.IsVisible(sectionSchema, DocumentSemesters.Of(JsonDocument.Parse(document.Payload).RootElement, facts)))
            return new(SectionUpdateProblem.HiddenSection, [], null);

        var payload = JsonNode.Parse(document.Payload)!.AsObject();
        var updated = payload.DeepClone().AsObject();
        // Очищенный раздел (null) уходит из содержимого: в схеме пустых значений нет.
        var sanitized = Sanitize(JsonNode.Parse(value.GetRawText()), sectionSchema, root, 0, _ => true);
        // Рисунок остаётся в тексте, если он связан с документом или принадлежит документу, который правящий читает
        // (вставка из буфера); такую связь добавляет это же сохранение. Проверка без сохранения базу не меняет.
        List<Guid> imagesToLink = [];
        if (!checkOnly && DocumentImages.IdsIn(sanitized) is { Count: > 0 } mentioned)
        {
            var (allowed, toLink) = await images.AllowedAsync(document, editorId, editorRoles, mentioned, ct);
            if (allowed.Count < mentioned.Count)
                sanitized = Sanitize(JsonNode.Parse(value.GetRawText()), sectionSchema, root, 0, allowed.Contains);
            imagesToLink = toLink.Where(DocumentImages.IdsIn(sanitized).Contains).ToList();
        }
        if (sanitized is not null) updated[section] = sanitized;
        else updated.Remove(section);

        // Поля из плана и закрытые статусом: присланное должно совпасть с тем, что уже в документе. Поля плана
        // открыты исправляющему (методист, администратор), закрытые статусом — нет.
        bool InSection(string p) => p == $"/{section}" || p.StartsWith($"/{section}/", StringComparison.Ordinal);
        var planPaths = PlanFields.Paths(schema, registry.FindSchema).Where(InSection).ToList();
        var locked = DocumentMetadata.ReadOnly(schema, document.Status, registry.FindSchema).Where(InSection)
            .Where(p => scope == DocumentEditScope.Content || !planPaths.Contains(p));
        var changed = locked
            .Where(p => !JsonNode.DeepEquals(At(payload, p), At(updated, p)))
            .Select(p => new DocumentValidationError(p, ReadOnlyChanged)).ToList();
        if (changed.Count > 0) return new(SectionUpdateProblem.ReadOnlyChanged, changed, null);
        // После приёма и у администратора — только поля плана: остальное содержимое раздела должно совпасть.
        if (scope == DocumentEditScope.PlanOnly && !JsonNode.DeepEquals(Without(payload[section], section, planPaths),
                Without(updated[section], section, planPaths)))
            return new(SectionUpdateProblem.ReadOnlyChanged, [new DocumentValidationError($"/{section}", PlanOnlyChanged)], null);
        var corrections = scope == DocumentEditScope.Content ? [] : PlanFields.Changes(payload, updated, planPaths).ToList();
        // Производное следует за исправлением (этап 5.2): вид курсового проектирования — за видами аттестации,
        // контактная работа — за часами занятий. Клиент получит содержимое обратно: он этих изменений не видел.
        var followed = scope == DocumentEditScope.Content ? []
            : PlanFields.FollowCourseWorkKind(schema, payload, updated, facts, registry.FindSchema)
                .Concat(PlanFields.FollowContactHours(payload, updated)).ToList();
        corrections.AddRange(followed);

        var updatedJson = updated.ToJsonString(Json);
        var element = JsonDocument.Parse(updatedJson).RootElement;
        var errors = validator.Validate(schemaJson, element, ValidationMode.Draft);
        // Правила об ошибках в самих данных (год издания в будущем) не дают сохранить и черновик — в этом разделе.
        var wrong = errors.Count > 0 ? errors : rules.Run(schema, element, facts, registry.FindSchema, new HashSet<string>(), onSave: true)
            .Where(e => DocumentSubmission.Section(e.Path) == section).ToList();
        if (checkOnly)
        {
            // Состояние разделов — по присланному, как если бы его сохранили: рамки и индикаторы без сохранения.
            var check = DocumentMetadata.Build(schemaJson, element, document.Status, facts, validator, rules, registry.FindSchema);
            return new(wrong.Count > 0 ? SectionUpdateProblem.Invalid : null, wrong, check.Sections.Single(s => s.Key == section),
                check.Sections, check.HiddenPaths, CommentMoves: (await RemapCommentsAsync(document.Id, section, payload[section],
                    updated[section], apply: false, ct)).Select(m => new CommentMove(m.Comment.Id, m.Place.Path, m.Place.RecordDeleted)).ToList());
        }
        if (wrong.Count > 0) return new(SectionUpdateProblem.Invalid, wrong, null);

        // Принятый документ после исправления должен оставаться полным: иначе — возврат автору, с подтверждением.
        var broken = corrections.Count > 0 && DocumentTransitions.CanReturnAfterPlanCorrection(document.Status)
            ? DocumentSubmission.Check(schemaJson, element, facts, validator, rules, registry.FindSchema)
                .Where(e => e.Level == IssueLevels.Error).ToList()
            : [];
        if (broken.Count > 0 && !confirmReturn) return new(SectionUpdateProblem.ReturnNotConfirmed, broken, null);

        await images.LinkAsync(document.Id, imagesToLink, ct);
        await RemapCommentsAsync(document.Id, section, payload[section], updated[section], apply: true, ct);
        if (scope == DocumentEditScope.PlanOnly) document.CorrectPlanFields(updatedJson);
        else if (document.Edit(updatedJson) is { } previous) history.StatusChanged(document, previous, editorId);
        var now = clock.GetUtcNow();
        if (broken.Count > 0) await ReturnToAuthorAsync(document, broken, schema, editorId, now, ct);
        foreach (var c in corrections)
            db.AuditLog.Add(new AuditLogEntry
            {
                UserId = editorId, EventType = AuditEvents.PlanFieldCorrected, EntityType = AuditEntities.Document,
                EntityId = document.Id, OccurredAt = now,
                Payload = new JsonObject { ["path"] = c.Path, ["before"] = c.Before, ["after"] = c.After }.ToJsonString(Json)
            });
        await history.SnapshotAsync(document, editorId, broken.Count > 0 ? VersionTriggers.StatusChange : VersionTriggers.AutoSave, ct);

        var metadata = DocumentMetadata.Build(schemaJson, element, document.Status, facts, validator, rules, registry.FindSchema);
        return new(null, [], metadata.Sections.Single(s => s.Key == section), metadata.Sections, metadata.HiddenPaths,
            followed.Count > 0 ? element : null);
    }

    // Замечания к записям списков следуют за своей записью (этап 8, A14); удалена запись — замечание остаётся, с признаком.
    // Сохраняет вызывающий, вместе с содержимым. Без apply (проверка без сохранения) замечания не меняются — только места.
    private async Task<List<(DocumentComment Comment, CommentPlaces.Place Place)>> RemapCommentsAsync(
        Guid documentId, string section, JsonNode? before, JsonNode? after, bool apply, CancellationToken ct)
    {
        var prefix = $"/{section}/";
        var comments = await db.DocumentComments
            .Where(c => c.DocumentId == documentId && !c.IsRecordDeleted && c.FieldPath.StartsWith(prefix)).ToListAsync(ct);
        var same = JsonNode.DeepEquals(before, after);
        var moves = comments.Select(c => (Comment: c, Place: same ? new CommentPlaces.Place(c.FieldPath, false)
            : CommentPlaces.Remap(before, after, c.FieldPath))).ToList();
        if (apply)
            foreach (var (comment, place) in moves)
                (comment.FieldPath, comment.IsRecordDeleted) = (place.Path, place.RecordDeleted);
        return moves;
    }

    // «Принят → На доработке»: замечание от исправившего к месту первой ошибки раздела — по одному на раздел, текст —
    // ошибки раздела; в истории — причина возврата. Автору — уведомление с числом замечаний (этап 6.1, C3).
    private async Task ReturnToAuthorAsync(
        Document document, IReadOnlyList<DocumentValidationError> errors, JsonElement schema, Guid editorId, DateTimeOffset now,
        CancellationToken ct)
    {
        var returnedAt = history.StatusChanged(document, document.ReturnAfterPlanCorrection(), editorId, ReturnedAfterPlanCorrection);
        var remarks = 0;
        foreach (var group in errors.GroupBy(e => DocumentSubmission.Section(e.Path)))
        {
            var place = group.First().Path;
            if (SchemaInspector.SubschemaAt(schema, place, registry.FindSchema) is null) place = $"/{group.Key}";
            db.DocumentComments.Add(new DocumentComment
            {
                DocumentId = document.Id, AuthorId = editorId, FieldPath = place, CreatedAt = now,
                Text = $"После исправления данных плана: {string.Join("; ", group.Select(e => e.Message).Distinct())}."
            });
            remarks++;
        }
        await notifications.ReturnedAfterPlanCorrectionAsync(document, editorId, remarks, returnedAt, ct);
    }

    /// <summary>«Сохранить»: новый снимок версии; черновик становится сохранённым черновиком.</summary>
    public async Task SaveAsync(Document document, Guid editorId, CancellationToken ct = default)
    {
        var previous = document.Save();
        if (previous is not null) history.StatusChanged(document, previous, editorId);
        await history.SnapshotAsync(document, editorId, VersionTriggers.ManualSave, ct);
    }

    /// <summary>
    /// Мягкое удаление черновика автором (этап 6, A5): история статусов и журнал аудита (NFR-SEC-07). Удалённый
    /// документ скрыт фильтром запросов — по дисциплине можно создать новый.
    /// </summary>
    public void Delete(Document document, Guid editorId)
    {
        var now = clock.GetUtcNow();
        var from = document.Delete(now);
        history.StatusChanged(document, from, editorId);
        db.AuditLog.Add(new AuditLogEntry
        {
            UserId = editorId, EventType = AuditEvents.DocumentDeleted, EntityType = AuditEntities.Document,
            EntityId = document.Id, OccurredAt = now,
            Payload = new JsonObject { ["title"] = document.Title, ["status"] = from }.ToJsonString(Json)
        });
    }

    private Task<string> SchemaAsync(Document document, CancellationToken ct) =>
        db.DocumentTemplates.IgnoreQueryFilters().Where(t => t.Id == document.TemplateId).Select(t => t.Schema).SingleAsync(ct);

    // Строки с x-ui-widget: richtext — через санитайзер (NFR-SEC-10); клиенту не доверяем.
    private JsonNode? Sanitize(JsonNode? node, JsonElement schema, JsonElement root, int depth, Func<Guid, bool> keepImage)
    {
        if (node is null || depth > 16) return node;
        if (node is JsonValue v && v.TryGetValue(out string? text) && schema.TryGetProperty("x-ui-widget", out var widget) &&
            widget.GetString() == "richtext")
            return sanitizer.Sanitize(text, keepImage);
        if (node is JsonObject o && schema.TryGetProperty("properties", out var props))
            foreach (var p in props.EnumerateObject())
            {
                var childRoot = root;
                if (o[p.Name] is { } child && SchemaInspector.Deref(p.Value, ref childRoot, registry.FindSchema) is { } childSchema)
                    o[p.Name] = Sanitize(child.DeepClone(), childSchema, childRoot, depth + 1, keepImage);
            }
        if (node is JsonArray a && schema.TryGetProperty("items", out var items))
        {
            var itemsRoot = root;
            if (SchemaInspector.Deref(items, ref itemsRoot, registry.FindSchema) is { } itemSchema)
                for (var i = 0; i < a.Count; i++)
                    a[i] = Sanitize(a[i]?.DeepClone(), itemSchema, itemsRoot, depth + 1, keepImage);
        }
        return node;
    }

    // Раздел без полей из плана — чтобы сравнить остальное содержимое.
    private static JsonNode? Without(JsonNode? sectionValue, string section, IEnumerable<string> planPaths)
    {
        if (sectionValue?.DeepClone() is not JsonObject copy) return sectionValue?.DeepClone();
        foreach (var path in planPaths)
        {
            var segments = path.TrimStart('/').Split('/')[1..];
            if (segments.Length == 0) return null;
            JsonNode? parent = copy;
            foreach (var segment in segments[..^1]) parent = (parent as JsonObject)?[segment];
            (parent as JsonObject)?.Remove(segments[^1]);
        }
        return copy;
    }

    private static JsonNode? At(JsonNode node, string pointer)
    {
        JsonNode? current = node;
        foreach (var raw in pointer.TrimStart('/').Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            current = current switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i < a.Count => a[i],
                _ => null
            };
            if (current is null) return null;
        }
        return current;
    }
}
