using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RpdFos.Api.Features.Imports;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Import;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>API импорта: загрузка файлов, фоновый разбор в очереди parsing, статус и замечания.</summary>
[Collection(DatabaseCollection.Name)]
public class ImportsApiTests(DatabaseFixture app)
{
    private static readonly string[] Fields = ["plan", "competences", "matrix", "positions"];

    [Fact]
    public async Task Methodist_uploads_real_files_and_import_runs_in_background()
    {
        var client = await MethodistClientAsync();

        var response = await client.PostAsync("/api/imports", Form(ImportFiles.Read()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<ImportAccepted>())!;
        Assert.Equal($"/api/imports/{accepted.Id}", response.Headers.Location?.OriginalString);

        var run = await WaitForCompletionAsync(client, accepted.Id);
        Assert.Equal(ParserRunStatuses.Partial, run.Status);
        Assert.Equal(new ImportCounts(46, 17, 37), run.Counts);
        Assert.Equal("plan-09.04.04-2023.xlsx", run.Files.Plan);
        Assert.NotNull(run.CurriculumPlanId);
        // Ошибка разбора — с файлом, таблицей и строкой.
        Assert.Contains(run.Issues, i => i is { Severity: ImportIssue.Error, File: "matrix-09.04.04.docx", Sheet: "Таблица 3", Row: 39 });

        var list = await client.GetFromJsonAsync<List<ImportRunItem>>("/api/imports");
        var item = Assert.Single(list!, r => r.Id == accepted.Id);
        Assert.Equal((1, run.Issues.Count - 1), (item.Errors, item.Warnings));
    }

    [Fact]
    public async Task Teacher_cannot_upload()
    {
        var response = await app.CreateClientFor(AppRoles.Teacher).PostAsync("/api/imports", Form(ImportFiles.Read()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Account_without_directory_record_cannot_upload()
    {
        // Методист по роли, но его учётная запись не связана со справочником сотрудников.
        var response = await app.CreateClientFor(AppRoles.Methodist, Guid.NewGuid().ToString())
            .PostAsync("/api/imports", Form(ImportFiles.Read()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_extension_is_422()
    {
        var files = ImportFiles.Read();
        files[0] = ("plan.docx", files[0].Content);

        var response = await (await MethodistClientAsync()).PostAsync("/api/imports", Form(files));

        await AssertRejectedAsync(response, "plan");
    }

    [Fact]
    public async Task Same_file_in_two_fields_is_422()
    {
        var files = ImportFiles.Read();
        files[2] = ("matrix-09.04.04.docx", files[1].Content); // список компетенций вместо матрицы

        var response = await (await MethodistClientAsync()).PostAsync("/api/imports", Form(files));

        await AssertRejectedAsync(response, "matrix");
    }

    [Fact]
    public async Task Missing_required_file_is_422()
    {
        var response = await (await MethodistClientAsync()).PostAsync("/api/imports", Form(ImportFiles.Read().Take(2).ToList()));

        await AssertRejectedAsync(response, "matrix");
    }

    // ---------- Помощники ----------

    private async Task<HttpClient> MethodistClientAsync()
    {
        Employee? methodist = null;
        await app.WithDbAsync(async db => methodist = await ImportFiles.AddMethodistAsync(db));
        return app.CreateClientFor(AppRoles.Methodist, methodist!.IdentitySubject);
    }

    private static MultipartFormDataContent Form(IReadOnlyList<(string Name, byte[] Content)> files)
    {
        var form = new MultipartFormDataContent();
        for (var i = 0; i < files.Count; i++)
        {
            var content = new ByteArrayContent(files[i].Content);
            content.Headers.ContentType = new MediaTypeHeaderValue(ImportFiles.All[i].Mime);
            form.Add(content, Fields[i], files[i].Name);
        }
        return form;
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys);
    }

    private static async Task<ImportRunDetails> WaitForCompletionAsync(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var run = (await client.GetFromJsonAsync<ImportRunDetails>($"/api/imports/{id}"))!;
            if (run.Status != ParserRunStatuses.Running || DateTime.UtcNow > deadline) return run;
            await Task.Delay(500);
        }
    }
}
