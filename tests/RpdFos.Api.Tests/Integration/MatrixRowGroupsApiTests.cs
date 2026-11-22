using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Imports;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Import;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Этап 7.1, шаг 1: одинаковые строки матрицы без дисциплины плана — одна строка блока. В реальной матрице строка 49
/// таблиц 1–3 — «Проект по дисциплине "Машинное обучение…"»: в плане такой нет, и привязывать приходилось дважды.
/// Строки в базу кладёт тест (как их кладёт импорт): настоящий план 09.04.04 такой пары не содержит. Тесты коллекции идут
/// по очереди, поэтому каждый убирает за собой строки и связи — другие тесты видят в плане одну строку этапа 5.2.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MatrixRowGroupsApiTests(DatabaseFixture app)
{
    private const string Elective = "Б1.В.ДВ.03.01";
    private const string Project = "Проект по дисциплине \"Машинное обучение и интеллектуальные системы\"";

    // То же название в другой записи: регистр, пробелы, вид кавычек — как их сглаживает сопоставление с планом (этап 5.4).
    private const string ProjectOtherWriting = "проект по  дисциплине «Машинное обучение и интеллектуальные системы»";

    [Fact]
    public async Task Same_rows_of_two_tables_are_one_group_applied_and_undone_together()
    {
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var runId = await RealRunIdAsync();
        var elective = await DocumentData.DisciplineAsync(app, Elective);
        var seeded = await SeedAsync(
            Row("Таблица 1", 49, Project, "3", "УК-1"),
            Row("Таблица 3", 49, ProjectOtherWriting, "3", "ПК-1", "ПК-2"),
            Row("Таблица 2", 20, Project, "4", "ПК-1"),
            Row("Таблица 4", 5, "Связь, уже стоящая у дисциплины", "6", "ПК-1"));
        try
        {
            var rows = await ListAsync(methodist, runId);
            // Те же название и семестры — одна строка блока: все места, все коды. Другие семестры — отдельная строка.
            var group = Assert.Single(rows, r => r.Semesters == "3" && r.Name == Project);
            Assert.Equal(["Таблица 1, строка 49", "Таблица 3, строка 49"], group.Places);
            Assert.Equal(["УК-1", "ПК-1", "ПК-2"], group.Codes);
            Assert.Equal(seeded[0], group.Id);
            var otherSemesters = Assert.Single(rows, r => r.Semesters == "4" && r.Name == Project);
            Assert.Equal(["Таблица 2, строка 20"], otherSemesters.Places);
            var existing = Assert.Single(rows, r => r.Name.StartsWith("Связь, уже"));

            // У дисциплины уже есть связь с ПК-1 — от другой привязки: привязка группы её не дублирует, отмена не снимает.
            Assert.Equal(HttpStatusCode.OK, (await Apply(methodist, existing.Id, elective)).StatusCode);
            Assert.Equal(["ПК-1"], await LinksAsync(elective));

            var applied = await Apply(methodist, group.Id, elective);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            Assert.Empty((await applied.Content.ReadFromJsonAsync<MatrixRowApplied>())!.Warnings);
            Assert.Equal(["ПК-1", "ПК-2", "УК-1"], await LinksAsync(elective));
            var linked = Assert.Single(await ListAsync(methodist, runId), r => r.Semesters == "3" && r.Name == Project);
            Assert.Equal(("linked", 2), (linked.Status, linked.Places.Count));
            Assert.Equal(elective, linked.DisciplineId);
            // Строка с другими семестрами не тронута; решены обе строки группы, у каждой — запись аудита.
            await app.WithDbAsync(async db =>
            {
                var both = await db.MatrixUnmatchedRows.Where(r => r.Id == seeded[0] || r.Id == seeded[1]).ToListAsync();
                Assert.All(both, r => Assert.Equal((MatrixRowStatuses.Linked, elective), (r.Status, r.DisciplineId)));
                Assert.Equal(MatrixRowStatuses.Pending, (await db.MatrixUnmatchedRows.SingleAsync(r => r.Id == seeded[2])).Status);
                // Добавлена привязкой только то, чего у дисциплины не было: УК-1 (таблица 1) и ПК-2 (таблица 3).
                Assert.Equal(2, both.Sum(r => r.AddedCompetenceIds.Count));
                var events = await db.AuditLog.Where(a => a.EventType == AuditEvents.MatrixRowLinked &&
                    (a.EntityId == seeded[0] || a.EntityId == seeded[1])).Select(a => a.EntityId!.Value).ToListAsync();
                Assert.Equal(new[] { seeded[0], seeded[1] }.Order(), events.Order());
            });

            // Новая РПД по дисциплине предзаполнена связями всех строк группы.
            var (author, _) = await DocumentData.UserAsync(app);
            var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, Elective);
            Assert.Equal(["ПК-1", "ПК-2", "УК-1"], created.Metadata.CompetenceCandidates.Select(c => c.Code).Order(StringComparer.Ordinal));

            // «Отменить» — для всей группы, снимает только добавленные группой связи.
            Assert.Equal(HttpStatusCode.NoContent, (await methodist.PostAsync($"/api/imports/matrix-rows/{linked.Id}/undo", null)).StatusCode);
            Assert.Equal(["ПК-1"], await LinksAsync(elective));
            var back = Assert.Single(await ListAsync(methodist, runId), r => r.Semesters == "3" && r.Name == Project);
            Assert.Equal(("pending", 2), (back.Status, back.Places.Count));
            await app.WithDbAsync(async db =>
            {
                var both = await db.MatrixUnmatchedRows.Where(r => r.Id == seeded[0] || r.Id == seeded[1]).ToListAsync();
                Assert.All(both, r => Assert.Equal((MatrixRowStatuses.Pending, null, 0), (r.Status, r.DisciplineId, r.AddedCompetenceIds.Count)));
                Assert.Equal(2, await db.AuditLog.CountAsync(a => a.EventType == AuditEvents.MatrixRowUnlinked &&
                    (a.EntityId == seeded[0] || a.EntityId == seeded[1])));
            });

            // Повторное «Применить» после отмены снова привязывает обе строки; «Не учитывать» тоже действует на группу.
            Assert.Equal(HttpStatusCode.OK, (await methodist.PostAsJsonAsync($"/api/imports/matrix-rows/{back.Id}/apply",
                new MatrixRowRequest(null, Ignore: true))).StatusCode);
            var ignored = Assert.Single(await ListAsync(methodist, runId), r => r.Semesters == "3" && r.Name == Project);
            Assert.Equal(("ignored", 2), (ignored.Status, ignored.Places.Count));
            Assert.Equal(HttpStatusCode.NoContent, (await methodist.PostAsync($"/api/imports/matrix-rows/{ignored.Id}/undo", null)).StatusCode);
            Assert.Equal(["ПК-1"], await LinksAsync(elective));
        }
        finally
        {
            await CleanUpAsync(seeded, elective);
        }
    }

    [Fact]
    public async Task Rows_resolved_differently_before_stay_separate()
    {
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var runId = await RealRunIdAsync();
        var elective = await DocumentData.DisciplineAsync(app, Elective);
        var seeded = await SeedAsync(
            Row("Таблица 1", 10, "Строка решённая по-разному", "5", "ПК-1"),
            Row("Таблица 3", 10, "Строка решённая по-разному", "5", "ПК-1"));
        try
        {
            // Вторая строка решена до этапа 7.1 отдельно от первой.
            await app.WithDbAsync(async db =>
            {
                var second = await db.MatrixUnmatchedRows.SingleAsync(r => r.Id == seeded[1]);
                (second.Status, second.DisciplineId) = (MatrixRowStatuses.Linked, elective);
                await db.SaveChangesAsync();
            });
            var rows = (await ListAsync(methodist, runId)).Where(r => r.Name == "Строка решённая по-разному").ToList();
            Assert.Equal([("pending", 1), ("linked", 1)], rows.Select(r => (r.Status, r.Places.Count)));

            // Привязка первой не трогает вторую — и отмена тоже.
            Assert.Equal(HttpStatusCode.OK, (await methodist.PostAsJsonAsync($"/api/imports/matrix-rows/{rows[0].Id}/apply",
                new MatrixRowRequest(null, Ignore: true))).StatusCode);
            await app.WithDbAsync(async db =>
                Assert.Equal(MatrixRowStatuses.Linked, (await db.MatrixUnmatchedRows.SingleAsync(r => r.Id == seeded[1])).Status));
            Assert.Equal(HttpStatusCode.NoContent, (await methodist.PostAsync($"/api/imports/matrix-rows/{rows[0].Id}/undo", null)).StatusCode);
            await app.WithDbAsync(async db =>
            {
                Assert.Equal(MatrixRowStatuses.Linked, (await db.MatrixUnmatchedRows.SingleAsync(r => r.Id == seeded[1])).Status);
                Assert.Equal(MatrixRowStatuses.Pending, (await db.MatrixUnmatchedRows.SingleAsync(r => r.Id == seeded[0])).Status);
            });
        }
        finally
        {
            await CleanUpAsync(seeded, elective);
        }
    }

    [Fact]
    public async Task Options_of_other_rows_change_order_after_binding_but_keep_their_ids()
    {
        // Основа выбора по идентификатору дисциплины (клиент — MatrixRowChoicesTests): после привязки дисциплина получила
        // связи и ушла из «без связей с матрицей» — в списках остальных строк она встала на другое место.
        var (methodist, _) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var runId = await RealRunIdAsync();
        var elective = await DocumentData.DisciplineAsync(app, Elective);
        var seeded = await SeedAsync(
            Row("Таблица 1", 30, "Первая строка для порядка", "3", "ПК-1"),
            Row("Таблица 1", 31, "Вторая строка для порядка", "5", "ПК-2"));
        try
        {
            static int IndexOf(MatrixRowState row, Guid id) => row.Options.ToList().FindIndex(o => o.Id == id);
            var before = Assert.Single(await ListAsync(methodist, runId), r => r.Name == "Вторая строка для порядка");
            Assert.True(before.Options.Single(o => o.Id == elective).WithoutMatrix);

            Assert.Equal(HttpStatusCode.OK, (await Apply(methodist, seeded[0], elective)).StatusCode);

            var after = Assert.Single(await ListAsync(methodist, runId), r => r.Name == "Вторая строка для порядка");
            Assert.False(after.Options.Single(o => o.Id == elective).WithoutMatrix);
            Assert.NotEqual(IndexOf(before, elective), IndexOf(after, elective));
            Assert.Equal(before.Options.Select(o => o.Id).Order(), after.Options.Select(o => o.Id).Order());
        }
        finally
        {
            await CleanUpAsync(seeded, elective);
        }
    }

    private async Task<Guid> RealRunIdAsync()
    {
        var planId = await app.RealPlanIdAsync();
        var id = Guid.Empty;
        await app.WithDbAsync(async db => id = await db.ParserRuns.Where(r => r.CreatedCurriculumPlanId == planId).Select(r => r.Id).SingleAsync());
        return id;
    }

    private static MatrixUnmatchedRow Row(string sheet, int row, string name, string semesters, params string[] codes) => new()
    {
        Sheet = sheet, RowNumber = row, Name = name, Semesters = semesters, Codes = codes.ToList(), CompetenceCodes = codes.ToList()
    };

    // Строки ложатся в базу как после импорта: у версии плана, созданной запуском настоящих файлов.
    private async Task<List<Guid>> SeedAsync(params MatrixUnmatchedRow[] rows)
    {
        var planId = await app.RealPlanIdAsync();
        await app.WithDbAsync(async db =>
        {
            foreach (var row in rows) row.CurriculumPlanId = planId;
            db.MatrixUnmatchedRows.AddRange(rows);
            await db.SaveChangesAsync();
        });
        return rows.Select(r => r.Id).ToList();
    }

    private static async Task<List<MatrixRowState>> ListAsync(HttpClient methodist, Guid runId) =>
        (await methodist.GetFromJsonAsync<List<MatrixRowState>>($"/api/imports/{runId}/matrix-rows"))!;

    private static Task<HttpResponseMessage> Apply(HttpClient methodist, Guid rowId, Guid disciplineId) =>
        methodist.PostAsJsonAsync($"/api/imports/matrix-rows/{rowId}/apply", new MatrixRowRequest(disciplineId));

    private async Task<List<string>> LinksAsync(Guid disciplineId)
    {
        var codes = new List<string>();
        await app.WithDbAsync(async db => codes = await (
            from m in db.CompetenceMatrix
            join c in db.Competences on m.CompetenceId equals c.Id
            where m.DisciplineId == disciplineId
            select c.Code).ToListAsync());
        return codes.Order(StringComparer.Ordinal).ToList();
    }

    // Базовое состояние плана — у факультатива нет связей с матрицей (первый тест этапа 5.2 на это опирается).
    private async Task CleanUpAsync(IEnumerable<Guid> rowIds, Guid disciplineId) =>
        await app.WithDbAsync(async db =>
        {
            var ids = rowIds.ToList();
            db.MatrixUnmatchedRows.RemoveRange(await db.MatrixUnmatchedRows.Where(r => ids.Contains(r.Id)).ToListAsync());
            db.CompetenceMatrix.RemoveRange(await db.CompetenceMatrix.Where(m => m.DisciplineId == disciplineId).ToListAsync());
            await db.SaveChangesAsync();
        });
}
