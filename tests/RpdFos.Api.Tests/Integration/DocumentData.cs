using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Сценарии документов на реальном плане 09.04.04: авторы, опубликованные шаблоны, дисциплины, создание.</summary>
public static class DocumentData
{
    /// <summary>Пользователь с ролью и своей записью справочника.</summary>
    public static async Task<(HttpClient Client, Employee Employee)> UserAsync(DatabaseFixture app, string role = AppRoles.Teacher)
    {
        Employee? employee = null;
        await app.WithDbAsync(async db => employee = await ImportFiles.AddMethodistAsync(db));
        return (app.CreateClientFor(role, employee!.IdentitySubject), employee);
    }

    public static async Task<DocumentTemplate> PublishedTemplateAsync(DatabaseFixture app)
    {
        var planId = await app.RealPlanIdAsync();
        DocumentTemplate? template = null;
        await app.WithDbAsync(async db => template = await TemplateData.AddPublishedAsync(db, planId));
        return template!;
    }

    public static async Task<Guid> DisciplineAsync(DatabaseFixture app, string code)
    {
        var planId = await app.RealPlanIdAsync();
        var id = Guid.Empty;
        await app.WithDbAsync(async db =>
            id = await db.Disciplines.Where(d => d.CurriculumPlanId == planId && d.Code == code).Select(d => d.Id).SingleAsync());
        return id;
    }

    public static async Task<DocumentDetails> CreateAsync(DatabaseFixture app, HttpClient client, Guid templateId, string code)
    {
        var response = await client.PostAsJsonAsync("/api/documents",
            new CreateDocumentRequest(templateId, await DisciplineAsync(app, code)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    public static async Task<SectionSaved> PatchAsync(HttpClient client, Guid documentId, string section, int version, JsonNode? value)
    {
        var response = await client.PatchAsJsonAsync($"/api/documents/{documentId}/sections/{section}",
            new SectionUpdateRequest(version, JsonDocument.Parse(value?.ToJsonString() ?? "null").RootElement));
        Assert.True(response.IsSuccessStatusCode, $"{section}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SectionSaved>())!;
    }

    /// <summary>
    /// Заполняет созданную РПД так, чтобы она прошла полную проверку: цели и задачи, по индикатору у каждой
    /// компетенции матрицы, самоподготовка на все часы самостоятельной работы, тема с часами плана, по записи
    /// в видимых условных разделах, основная литература. Возвращает текущую версию документа.
    /// </summary>
    public static async Task<int> FillForSubmissionAsync(HttpClient client, DocumentDetails created)
    {
        var id = created.Document.Id;
        var version = created.Document.Version;
        var plan = Sums(created.Payload);
        var visible = created.Metadata.Sections.Where(s => s.Visible).Select(s => s.Key).ToHashSet();

        var sections = new Dictionary<string, JsonNode>
        {
            ["hours_distribution"] = WithSelfStudy(created.Payload, new JsonObject { ["kind"] = "самоподготовка" }),
            ["goals_and_objectives"] = new JsonObject
            {
                ["goals"] = "<p>Сформировать у обучающихся умения строить и исследовать математические модели систем.</p>",
                ["objectives"] = new JsonArray("Освоить методы построения моделей систем")
            },
            ["competences"] = new JsonArray(created.Metadata.CompetenceCandidates.Where(c => c.Indicators.Count > 0)
                .Select(c => (JsonNode)new JsonObject
                {
                    ["code"] = c.Code, ["formulation"] = c.Formulation,
                    ["indicators"] = new JsonArray(new JsonObject { ["code"] = c.Indicators[0].Code, ["text"] = c.Indicators[0].Text })
                }).ToArray()),
            // По теме на семестр трудоёмкости — с его часами (у тем есть семестр, этап 5.2).
            ["thematic_plan"] = new JsonArray(created.Payload.GetProperty("hours_distribution").GetProperty("semesters").EnumerateArray()
                .Select((s, i) => (JsonNode)new JsonObject
                {
                    ["section_number"] = i + 1, ["semester"] = s.GetProperty("semester").GetInt32(),
                    ["topic_title"] = $"Основы моделирования систем, часть {i + 1}",
                    ["hours"] = new JsonObject
                    {
                        ["lectures"] = Int(s, "lectures"), ["practice"] = Int(s, "practice"), ["lab_hours"] = Int(s, "lab_hours"),
                        ["self_study"] = Int(s, "self_study")
                    }
                }).ToArray()),
            ["literature"] = new JsonObject
            {
                ["main"] = new JsonArray(new JsonObject
                {
                    ["citation"] = "Петров П. П. Моделирование систем : учебник. — Москва : Наука, 2024. — 320 с.",
                    ["year"] = DateTime.Today.Year - 1
                }),
                ["additional"] = new JsonArray()
            }
        };
        if (visible.Contains("practical_classes"))
            sections["practical_classes"] = new JsonArray(new JsonObject { ["number"] = 1, ["title"] = "Построение модели", ["hours"] = plan.Practice });
        if (visible.Contains("lab_works"))
            sections["lab_works"] = new JsonArray(new JsonObject { ["number"] = 1, ["title"] = "Исследование модели", ["hours"] = plan.Labs });
        if (visible.Contains("course_work"))
            sections["course_work"] = new JsonObject { ["kind"] = "course_project", ["topics"] = new JsonArray("Модель системы массового обслуживания") };

        // Должности составителя в справочнике тестов нет — автор вводит её сам.
        var approval = JsonNode.Parse(created.Payload.GetProperty("approval_sheet").GetRawText())!;
        foreach (var developer in approval["developers"]!.AsArray().OfType<JsonObject>())
            developer["position"] ??= "доцент";
        sections["approval_sheet"] = approval;

        foreach (var (section, value) in sections)
            version = (await PatchAsync(client, id, section, version, value)).Version;
        return version;
    }

    /// <summary>
    /// Трудоёмкость документа с видами самостоятельной работы: последний вид получает остаток часов самостоятельной
    /// работы каждого семестра, остальные — свои часы как есть.
    /// </summary>
    public static JsonObject WithSelfStudy(JsonElement payload, params JsonObject[] kinds)
    {
        var hours = JsonNode.Parse(payload.GetProperty("hours_distribution").GetRawText())!.AsObject();
        var last = kinds[^1];
        var rest = new JsonArray();
        foreach (var semester in hours["semesters"]!.AsArray())
        {
            var number = (int)semester!["semester"]!;
            var taken = kinds[..^1].SelectMany(k => k["hours"]?.AsArray() ?? [])
                .Where(h => (int)h!["semester"]! == number).Sum(h => (int)h!["hours"]!);
            var left = ((int?)semester["self_study"] ?? 0) - taken;
            if (left > 0) rest.Add(new JsonObject { ["semester"] = number, ["hours"] = left });
        }
        last["hours"] = rest;
        hours["self_study_kinds"] = new JsonArray(kinds.Select(k => (JsonNode)k).ToArray());
        return hours;
    }

    /// <summary>Часы плана по видам занятий — сумма по семестрам трудоёмкости документа.</summary>
    public static (int Lectures, int Practice, int Labs, int SelfStudy) Sums(JsonElement payload)
    {
        var semesters = payload.GetProperty("hours_distribution").GetProperty("semesters").EnumerateArray().ToList();
        int Sum(string key) => semesters.Sum(s => s.TryGetProperty(key, out var v) ? v.GetInt32() : 0);
        return (Sum("lectures"), Sum("practice"), Sum("lab_hours"), Sum("self_study"));
    }

    private static int Int(JsonElement node, string key) => node.TryGetProperty(key, out var v) ? v.GetInt32() : 0;
}
