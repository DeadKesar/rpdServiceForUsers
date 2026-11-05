using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RpdFos.Api.Features.Imports;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Import;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Этап 5.2, B6: строки матрицы без дисциплины плана — сохраняются импортом и привязываются вручную. В реальных
/// файлах это «Внедрение и эксплуатация информационных систем» (ПК-1, ПК-2), а электив Б1.В.ДВ.03.01 — без связей.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MatrixRowsApiTests(DatabaseFixture app)
{
    private const string Elective = "Б1.В.ДВ.03.01";

    [Fact]
    public async Task Methodist_links_row_new_rpd_gets_its_competences_and_undo_removes_them()
    {
        var (methodist, record) = await DocumentData.UserAsync(app, AppRoles.Methodist);
        var runId = await RealRunIdAsync();
        var rows = (await methodist.GetFromJsonAsync<List<MatrixRowState>>($"/api/imports/{runId}/matrix-rows"))!;
        var row = Assert.Single(rows);
        Assert.Equal(("Внедрение и эксплуатация информационных систем", "pending"), (row.Name, row.Status));
        Assert.Equal(["ПК-1", "ПК-2"], row.Codes);
        // В выборе — без модулей, групп элективов и ГИА; дисциплины без связей с матрицей — раньше остальных.
        var elective = Assert.Single(row.Options, o => o.Code == Elective);
        Assert.True(elective.WithoutMatrix);
        Assert.DoesNotContain(row.Options, o => o.Code is "Б1.О.14" or "Б1.В.ДВ.03" || o.Code.StartsWith("Б3"));
        Assert.True(row.Options.TakeWhile(o => o.WithoutMatrix).Count() >= 1);
        Assert.All(row.Options.SkipWhile(o => o.WithoutMatrix), o => Assert.False(o.WithoutMatrix));

        var applied = await methodist.PostAsJsonAsync($"/api/imports/matrix-rows/{row.Id}/apply", new MatrixRowRequest(elective.Id));
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Empty((await applied.Content.ReadFromJsonAsync<MatrixRowApplied>())!.Warnings);
        var linked = Assert.Single((await methodist.GetFromJsonAsync<List<MatrixRowState>>($"/api/imports/{runId}/matrix-rows"))!);
        Assert.Equal(("linked", $"{Elective} {elective.Name}", record.ShortName), (linked.Status, linked.Discipline, linked.ResolvedBy));
        // Повторная привязка — только после отмены.
        Assert.Equal(HttpStatusCode.Conflict,
            (await methodist.PostAsJsonAsync($"/api/imports/matrix-rows/{row.Id}/apply", new MatrixRowRequest(elective.Id))).StatusCode);

        // Новая РПД по дисциплине предзаполняется связями привязки.
        var (author, _) = await DocumentData.UserAsync(app);
        var created = await DocumentData.CreateAsync(app, author, (await DocumentData.PublishedTemplateAsync(app)).Id, Elective);
        Assert.Equal(["ПК-1", "ПК-2"], created.Metadata.CompetenceCandidates.Select(c => c.Code).Order());

        // Отмена удаляет связи, добавленные привязкой; «Не учитывать» — решение без связей.
        Assert.Equal(HttpStatusCode.NoContent, (await methodist.PostAsync($"/api/imports/matrix-rows/{row.Id}/undo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await methodist.PostAsJsonAsync($"/api/imports/matrix-rows/{row.Id}/apply", new MatrixRowRequest(null, Ignore: true))).StatusCode);
        var ignored = Assert.Single((await methodist.GetFromJsonAsync<List<MatrixRowState>>($"/api/imports/{runId}/matrix-rows"))!);
        Assert.Equal(("ignored", null), (ignored.Status, ignored.DisciplineId));
        Assert.Equal(HttpStatusCode.NoContent, (await methodist.PostAsync($"/api/imports/matrix-rows/{row.Id}/undo", null)).StatusCode);

        await app.WithDbAsync(async db =>
        {
            Assert.False(await db.CompetenceMatrix.AnyAsync(m => m.DisciplineId == elective.Id));
            var events = await db.AuditLog.Where(a => a.EntityId == row.Id).Select(a => a.EventType).ToListAsync();
            Assert.Equal(2, events.Count(e => e == AuditEvents.MatrixRowLinked));
            Assert.Equal(2, events.Count(e => e == AuditEvents.MatrixRowUnlinked));
            Assert.Equal(MatrixRowStatuses.Pending, (await db.MatrixUnmatchedRows.SingleAsync(r => r.Id == row.Id)).Status);
        });
    }

    [Fact]
    public async Task Teacher_cannot_link_matrix_rows()
    {
        var runId = await RealRunIdAsync();
        Guid rowId = default;
        await app.WithDbAsync(async db => rowId = await db.MatrixUnmatchedRows.Select(r => r.Id).FirstAsync());
        var (teacher, _) = await DocumentData.UserAsync(app);

        Assert.Equal(HttpStatusCode.Forbidden, (await teacher.GetAsync($"/api/imports/{runId}/matrix-rows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teacher.PostAsJsonAsync($"/api/imports/matrix-rows/{rowId}/apply", new MatrixRowRequest(null, Ignore: true))).StatusCode);
    }

    private async Task<Guid> RealRunIdAsync()
    {
        var planId = await app.RealPlanIdAsync();
        var id = Guid.Empty;
        await app.WithDbAsync(async db => id = await db.ParserRuns.Where(r => r.CreatedCurriculumPlanId == planId).Select(r => r.Id).SingleAsync());
        return id;
    }
}
