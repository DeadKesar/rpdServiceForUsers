using System.Text.Json;
using System.Text.Json.Nodes;
using RpdFos.Core.DocumentTypes;

namespace RpdFos.Core.Documents;

/// <summary>Автор документа — запись справочника сотрудников: в составители попадает подпись и должность на момент ввода.</summary>
public sealed record DocumentAuthor(Guid Id, string NameForDocument, string? Position);

/// <summary>
/// Предзаполнение содержимого нового документа по <c>x-source</c> схемы: титул, место в структуре, трудоёмкость
/// и виды аттестации — из плана и справочников, компетенции — из матрицы (индикаторы выбирает автор),
/// первый составитель — автор. Источники, которые нужны только печати (курс, квалификация, год документа),
/// значения в содержимое не дают.
/// </summary>
public static class DocumentPrefill
{
    public static JsonObject Build(JsonElement schema, DisciplineFacts facts, DocumentAuthor author, Func<string, JsonElement?> resolve)
    {
        var root = schema;
        return SchemaInspector.Deref(schema, ref root, resolve) is { } node
            ? Fill(node, root, facts, author, resolve, 0) ?? []
            : [];
    }

    private static JsonObject? Fill(JsonElement node, JsonElement root, DisciplineFacts facts, DocumentAuthor author,
        Func<string, JsonElement?> resolve, int depth)
    {
        if (depth > 16 || !node.TryGetProperty("properties", out var properties)) return null;
        var result = new JsonObject();
        foreach (var p in properties.EnumerateObject())
        {
            var childRoot = root;
            if (SchemaInspector.Deref(p.Value, ref childRoot, resolve) is not { ValueKind: JsonValueKind.Object } child) continue;
            var source = Source(p.Value) ?? Source(child);
            var value = source is null ? Fill(child, childRoot, facts, author, resolve, depth + 1) : Value(source, child, facts, author);
            if (value is not null && (value is not JsonObject o || o.Count > 0)) result[p.Name] = value;
        }
        return result;
    }

    private static string? Source(JsonElement node) =>
        node.TryGetProperty("x-source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

    private static JsonNode? Value(string source, JsonElement field, DisciplineFacts facts, DocumentAuthor author)
    {
        var d = facts.Discipline;
        return source switch
        {
            "parser.discipline.name" => d.Name,
            "parser.discipline.ind" => d.Code,
            "parser.discipline.block" => PlanTitles.Block(d.Block),
            "parser.discipline.part_type" => PlanTitles.Part(d.PartType),
            "parser.discipline.hours_total" => d.TotalHours,
            "parser.discipline.ze_total" => d.TotalCredits,
            "parser.discipline.semesters" => new JsonArray(facts.Semesters.Select(s => (JsonNode)s.Semester).ToArray()),
            "parser.discipline.details" => new JsonArray(facts.Semesters.Select(s => (JsonNode)new JsonObject
            {
                ["semester"] = s.Semester,
                ["contact_hours"] = s.ContactHours,
                ["lectures"] = s.Lectures,
                ["practice"] = s.Practice,
                ["lab_hours"] = s.LabHours,
                ["self_study"] = s.SelfStudy,
                ["control"] = s.Control,
                ["credits"] = s.Credits,
                ["control_types"] = new JsonArray(s.ControlTypes.Select(t => (JsonNode)t).ToArray())
            }).ToArray()),
            "reference.direction.code" => facts.Direction.Code,
            "reference.direction.name" => facts.Direction.Name,
            "reference.direction.level" => PlanTitles.EducationLevel(facts.Direction.EducationLevel),
            "reference.profile.name" => facts.Profile.Name,
            "reference.curriculum_plan.education_form" => PlanTitles.StudyForm(facts.Plan.StudyForm),
            "reference.curriculum_plan.academic_year" =>
                PlanTitles.AcademicYear(facts.Plan.Year, facts.Semesters.Select(s => s.Semester).DefaultIfEmpty(1).Min()),
            "reference.department.name" => facts.Department?.Name,
            // Компетенции из матрицы — с пустым выбором индикаторов: индикаторы выбирает автор из кандидатов.
            "db.competence_matrix.by_discipline" => new JsonArray(facts.Competences.Select(c => (JsonNode)new JsonObject
            {
                ["code"] = c.Code,
                ["formulation"] = c.Formulation,
                ["indicators"] = new JsonArray()
            }).ToArray()),
            "db.user.current" => Developer(author, field),
            _ => null
        };
    }

    // Список составителей начинается с автора; поле-объект (схемы до этапа 4) — сам автор.
    private static JsonNode Developer(DocumentAuthor author, JsonElement field)
    {
        var developer = new JsonObject { ["user_id"] = author.Id.ToString(), ["name_for_document"] = author.NameForDocument };
        if (author.Position is not null) developer["position"] = author.Position;
        return field.TryGetProperty("type", out var type) && type.GetString() == "array" ? new JsonArray(developer) : developer;
    }
}
