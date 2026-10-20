using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Нормализованные часы и привязки к компетенциям пересобираются по payload в том же сохранении.</summary>
[Collection(DatabaseCollection.Name)]
public class DocumentProjectionsTests(DatabaseFixture app)
{
    [Fact]
    public async Task Hours_and_competence_links_follow_payload()
    {
        var planId = await app.RealPlanIdAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var projections = scope.ServiceProvider.GetRequiredService<DocumentProjections>();

        // «Математическое и компьютерное моделирование»: в матрице — УК-1, ОПК-1, ОПК-4.
        var discipline = await db.Disciplines.SingleAsync(d => d.CurriculumPlanId == planId && d.Code == "Б1.О.10");
        var ids = await (from m in db.CompetenceMatrix where m.DisciplineId == discipline.Id
                         join c in db.Competences on m.CompetenceId equals c.Id select new { c.Code, c.Id })
            .ToDictionaryAsync(c => c.Code, c => c.Id);
        var uk1Indicator = await db.CompetenceIndicators.Where(i => i.CompetenceId == ids["УК-1"]).OrderBy(i => i.Code).FirstAsync();
        var document = await AddDocumentAsync(db, planId, discipline.Id);

        document.Payload = $$"""
            {
              "hours_distribution": { "semesters": [ { "semester": 1, "lectures": 24, "practice": 8, "lab_hours": 16, "self_study": 24, "control": 36 } ] },
              "competences": [
                { "code": "УК-1", "indicators": [ { "code": "{{uk1Indicator.Code}}" } ] },
                { "code": "ОПК-1", "indicators": [] },
                { "code": "ПК-99", "indicators": [ { "code": "ПК-99.1" } ] }
              ]
            }
            """;
        await projections.SyncAsync(document);
        await db.SaveChangesAsync();

        var hours = Assert.Single(await db.DocumentHours.Where(h => h.DocumentId == document.Id).ToListAsync());
        Assert.Equal((1, 24, 8, 16, 24, 36), (hours.Semester, hours.Lectures, hours.Practice, hours.LabHours, hours.SelfStudy, hours.Control));
        // Выбранный индикатор — (компетенция, индикатор); без индикаторов — (компетенция, null); кода нет в матрице — нет привязки.
        Assert.Equal(
            new HashSet<(Guid?, Guid?)> { (ids["УК-1"], uk1Indicator.Id), (ids["ОПК-1"], null) },
            (await db.DocumentCompetenceLinks.Where(l => l.DocumentId == document.Id).ToListAsync())
                .Select(l => (l.CompetenceId, l.IndicatorId)).ToHashSet());

        // Повторное сохранение: семестр обновляется на месте, новый добавляется, лишние привязки уходят.
        document.Payload = """
            {
              "hours_distribution": { "semesters": [ { "semester": 1, "lectures": 20 }, { "semester": 2, "practice": 4 } ] },
              "competences": [ { "code": "ОПК-4" } ]
            }
            """;
        await projections.SyncAsync(document);
        await db.SaveChangesAsync();

        Assert.Equal([(1, 20, 0), (2, 0, 4)],
            (await db.DocumentHours.Where(h => h.DocumentId == document.Id).OrderBy(h => h.Semester).ToListAsync())
                .Select(h => (h.Semester, h.Lectures, h.Practice)));
        var link = Assert.Single(await db.DocumentCompetenceLinks.Where(l => l.DocumentId == document.Id).ToListAsync());
        Assert.Equal((ids["ОПК-4"], (Guid?)null), (link.CompetenceId, link.IndicatorId));
    }

    private static async Task<Document> AddDocumentAsync(AppDbContext db, Guid planId, Guid disciplineId)
    {
        var author = await ImportFiles.AddMethodistAsync(db);
        var template = DocumentTemplate.Create(new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd), "РПД", planId, author.Id);
        var document = new Document
        {
            TemplateId = template.Id, TemplateRevision = template.Revision, AuthorId = author.Id, DisciplineId = disciplineId,
            Title = "РПД"
        };
        db.AddRange(template, document);
        await db.SaveChangesAsync();
        return document;
    }
}
