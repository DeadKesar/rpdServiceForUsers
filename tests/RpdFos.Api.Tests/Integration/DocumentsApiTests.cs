using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Api.Features.Documents;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;
using RpdFos.Core.DocumentTypes;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Создание РПД по опубликованному шаблону для дисциплины реального плана 09.04.04 и предзаполнение (B3).</summary>
[Collection(DatabaseCollection.Name)]
public class DocumentsApiTests(DatabaseFixture app)
{
    [Fact]
    public async Task Teacher_creates_rpd_prefilled_from_plan()
    {
        var (client, teacher) = await TeacherAsync();
        var template = await PublishedTemplateAsync();

        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(template.Id, await DisciplineAsync("Б1.О.10")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
        Assert.Equal((DocumentStatuses.Draft, "Черновик", template.Revision), (created.Document.Status, created.Document.StatusTitle, created.Document.TemplateRevision));
        Assert.Equal("РПД «Математическое и компьютерное моделирование», набор 2023", created.Document.Title);

        // Титул и место в структуре — из плана и справочников.
        var p = created.Payload;
        var title = p.GetProperty("title_page");
        Assert.Equal(("Математическое и компьютерное моделирование", "Б1.О.10", "09.04.04", "Магистратура", "очная", "2023/2024"),
            (S(title, "discipline_name"), S(title, "discipline_code"), S(title, "direction_code"), S(title, "education_level"),
                S(title, "study_form"), S(title, "academic_year")));
        Assert.False(string.IsNullOrEmpty(S(title, "department_name")));
        var place = p.GetProperty("place_in_structure");
        Assert.Equal(("Б1.О.10", "Блок 1 «Дисциплины (модули)»", "Обязательная часть"), (S(place, "index"), S(place, "block"), S(place, "part")));

        // Трудоёмкость и виды аттестации.
        var hours = p.GetProperty("hours_distribution");
        Assert.Equal((3, 108), (hours.GetProperty("total_credits").GetInt32(), hours.GetProperty("total_hours").GetInt32()));
        var semester = Assert.Single(hours.GetProperty("semesters").EnumerateArray());
        Assert.Equal((1, 24, 8, 16, 24, 36), (I(semester, "semester"), I(semester, "lectures"), I(semester, "practice"),
            I(semester, "lab_hours"), I(semester, "self_study"), I(semester, "control")));
        Assert.Equal(["exam"], semester.GetProperty("control_types").EnumerateArray().Select(t => t.GetString()));

        // Компетенции матрицы — без выбранных индикаторов; кандидаты с индикаторами — в метаданных.
        Assert.Equal(["УК-1", "ОПК-1", "ОПК-4"], p.GetProperty("competences").EnumerateArray().Select(c => S(c, "code")));
        Assert.All(p.GetProperty("competences").EnumerateArray(), c => Assert.Equal(0, c.GetProperty("indicators").GetArrayLength()));
        Assert.Equal(["УК-1", "ОПК-1", "ОПК-4"], created.Metadata.CompetenceCandidates.Select(c => c.Code));
        Assert.All(created.Metadata.CompetenceCandidates, c => Assert.NotEmpty(c.Indicators));

        // Первый составитель — автор.
        var developer = Assert.Single(p.GetProperty("approval_sheet").GetProperty("developers").EnumerateArray());
        Assert.Equal(teacher.Id.ToString(), S(developer, "user_id"));

        // Поля из плана — только для чтения.
        Assert.Contains("/title_page/discipline_name", created.Metadata.ReadOnlyPaths);
        Assert.Contains("/hours_distribution/semesters", created.Metadata.ReadOnlyPaths);
        Assert.DoesNotContain("/hours_distribution", created.Metadata.ReadOnlyPaths); // виды самостоятельной работы — автора
        Assert.Contains("/place_in_structure/block", created.Metadata.ReadOnlyPaths);
        Assert.DoesNotContain("/title_page/academic_year", created.Metadata.ReadOnlyPaths);
        // Не предусмотрено планом: курсовой проект. Ссылка на указания к курсовому проектированию и самостоятельной
        // работе нужна всегда — самостоятельная работа есть у любой дисциплины.
        Assert.Equal(["/course_work"], created.Metadata.HiddenPaths.Order());

        // Предзаполненное содержимое — корректный черновик; история и нормализованные часы записаны тем же сохранением.
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<IDocumentValidator>()
            .Validate(new DocumentTypeRegistry().Get(DocumentTypeRegistry.Rpd).SchemaJson, p, ValidationMode.Draft));
        await app.WithDbAsync(async db =>
        {
            var change = Assert.Single(await db.DocumentStatusHistory.Where(h => h.DocumentId == created.Document.Id).ToListAsync());
            Assert.Equal(((string?)null, DocumentStatuses.Draft, teacher.Id), (change.FromStatus, change.ToStatus, change.ChangedBy));
            var row = Assert.Single(await db.DocumentHours.Where(h => h.DocumentId == created.Document.Id).ToListAsync());
            Assert.Equal((1, 16), (row.Semester, row.LabHours));
        });
    }

    [Fact]
    public async Task Plan_neighbours_follow_semesters()
    {
        // Б1.В.01 — во 2 семестре: раньше — только дисциплины и практики 1 семестра, позже — с 3 семестра.
        var (client, _) = await TeacherAsync();
        var created = await CreateAsync(client, (await PublishedTemplateAsync()).Id, "Б1.В.01");
        var earlier = created.Metadata.PlanDisciplines.Earlier.Select(d => d.Code).ToList();
        var later = created.Metadata.PlanDisciplines.Later.Select(d => d.Code).ToList();
        // Кандидаты есть — формулировки «выбирать не из чего» нет.
        Assert.Empty(created.Metadata.NoCandidatesTexts!);

        // Порядок плана: обязательная часть, вариативная, затем практики. Учебная практика 1–2 семестров не раньше.
        Assert.Equal(["Б1.О.01", "Б1.О.03", "Б1.О.08", "Б1.О.09", "Б1.О.10", "Б1.В.02", "Б1.В.03", "Б2.О.02(У)"], earlier);
        // Элективы и дисциплины модуля — да; модуль, группа элективов и ГИА — нет.
        Assert.Contains("Б1.В.ДВ.02.01", later);
        Assert.Contains("Б1.О.14.02", later);
        Assert.Contains("Б2.О.04(Пд)", later);
        Assert.DoesNotContain(later, c => c is "Б1.О.14" or "Б1.В.ДВ.02" or "Б3.01(Д)" or "Б1.В.01");
        Assert.All(created.Metadata.PlanDisciplines.Later, d => Assert.True(d.Semesters.Min() > 2));
    }

    [Theory]
    [InlineData("Б1.О.10", true, true, false)]   // лабораторные и практика, экзамен
    [InlineData("Б1.О.01", false, true, false)]  // лекции и практика, зачёт
    [InlineData("Б1.В.01", false, true, true)]   // проект по дисциплине: курсовой проект
    public async Task Conditional_sections_follow_plan(string code, bool labs, bool practice, bool courseWork)
    {
        var (client, _) = await TeacherAsync();
        var template = await PublishedTemplateAsync();

        var created = await CreateAsync(client, template.Id, code);

        var sections = created.Metadata.Sections.ToDictionary(s => s.Key);
        Assert.Equal((labs, practice, courseWork),
            (sections["lab_works"].Visible, sections["practical_classes"].Visible, sections["course_work"].Visible));
        // Скрытый раздел — «не предусмотрен учебным планом» со стандартной заглушкой.
        Assert.All(sections.Values.Where(s => !s.Visible), s =>
        {
            Assert.Equal(SectionFill.NotApplicable, s.Fill);
            Assert.Contains("не предусмотрен", s.StubText);
        });
        // Трудоёмкость из плана, но виды самостоятельной работы расписывает автор — до этого раздел не заполнен.
        Assert.Equal(SectionFill.Incomplete, sections["hours_distribution"].Fill);
        var missing = Assert.Single(sections["hours_distribution"].Missing);
        Assert.Equal("/hours_distribution/self_study_kinds", missing.Path);
        Assert.Contains("по видам 0 ч", missing.Message);
        Assert.Equal(SectionFill.Incomplete, sections["goals_and_objectives"].Fill);
        // Видимый условный раздел обязателен при отправке — хотя бы одна запись; новый документ его ещё не заполнил.
        Assert.All(sections.Values.Where(s => s.Visible && s.Key is "lab_works" or "practical_classes"), s =>
        {
            Assert.Equal(SectionFill.Incomplete, s.Fill);
            Assert.Equal(DocumentSubmission.EmptyVisibleSection, Assert.Single(s.Missing).Message);
        });
        // Курсовой: вид — из плана, примерные темы пишет автор.
        if (courseWork)
        {
            Assert.Equal("course_project", created.Payload.GetProperty("course_work").GetProperty("kind").GetString());
            Assert.Equal(SectionFill.Incomplete, sections["course_work"].Fill);
            Assert.Equal("/course_work/topics", Assert.Single(sections["course_work"].Missing).Path);
        }
    }

    [Theory]
    [InlineData("Б1.О.14", "модуль")]
    [InlineData("Б1.В.ДВ.01", "группа дисциплин по выбору")]
    [InlineData("Б2.О.01(У)", "практика")]
    [InlineData("Б3.01(Д)", "государственная итоговая аттестация")]
    public async Task Not_a_discipline_is_422(string code, string reason)
    {
        var (client, _) = await TeacherAsync();
        var template = await PublishedTemplateAsync();

        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(template.Id, await DisciplineAsync(code)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(reason, (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail);
    }

    [Theory]
    [InlineData("Б1.В.ДВ.01.01")] // дисциплина по выбору
    [InlineData("ФТД.В.01")]      // факультатив
    public async Task Elective_and_optional_disciplines_get_documents(string code)
    {
        var (client, _) = await TeacherAsync();
        var template = await PublishedTemplateAsync();

        var created = await CreateAsync(client, template.Id, code);

        Assert.Equal(code, created.Document.DisciplineCode);
    }

    [Fact]
    public async Task Second_document_for_discipline_and_lineage_is_409_with_link()
    {
        var (client, _) = await TeacherAsync();
        var (other, _) = await TeacherAsync();
        var template = await PublishedTemplateAsync();
        var first = await CreateAsync(client, template.Id, "Б1.О.03");

        // Другой преподаватель — тот же отказ: документ на дисциплину и линию шаблона один.
        var response = await other.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(template.Id, await DisciplineAsync("Б1.О.03")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(first.Document.Id.ToString(), ((JsonElement)problem.Extensions["documentId"]!).GetString());
        // По другому шаблону того же плана — можно.
        var another = await PublishedTemplateAsync();
        Assert.Equal("Б1.О.03", (await CreateAsync(other, another.Id, "Б1.О.03")).Document.DisciplineCode);
    }

    [Fact]
    public async Task Only_published_rpd_template_creates_documents()
    {
        var (client, _) = await TeacherAsync();
        var planId = await app.RealPlanIdAsync();
        DocumentTemplate? draft = null, fos = null;
        await app.WithDbAsync(async db =>
        {
            draft = await TemplateData.AddDraftAsync(db);
            fos = await TemplateData.AddPublishedAsync(db, planId, DocumentTypeRegistry.Fos);
        });
        var discipline = await DisciplineAsync("Б1.О.05");

        var fromDraft = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(draft!.Id, discipline));
        var fromFos = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(fos!.Id, discipline));
        var foreign = await client.PostAsJsonAsync("/api/documents",
            new CreateDocumentRequest((await PublishedTemplateAsync()).Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, fromDraft.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fromFos.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
    }

    [Fact]
    public async Task Author_needs_role_and_directory_record()
    {
        var template = await PublishedTemplateAsync();
        var request = new CreateDocumentRequest(template.Id, await DisciplineAsync("Б1.О.06"));

        var approver = await app.CreateClientFor(AppRoles.Approver).PostAsJsonAsync("/api/documents", request);
        var unlinked = await app.CreateClientFor(AppRoles.Teacher, Guid.NewGuid().ToString()).PostAsJsonAsync("/api/documents", request);

        Assert.Equal(HttpStatusCode.Forbidden, approver.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unlinked.StatusCode);
    }

    private async Task<(HttpClient Client, Employee Teacher)> TeacherAsync()
    {
        Employee? teacher = null;
        await app.WithDbAsync(async db => teacher = await ImportFiles.AddMethodistAsync(db));
        return (app.CreateClientFor(AppRoles.Teacher, teacher!.IdentitySubject), teacher);
    }

    private async Task<DocumentTemplate> PublishedTemplateAsync()
    {
        var planId = await app.RealPlanIdAsync();
        DocumentTemplate? template = null;
        await app.WithDbAsync(async db => template = await TemplateData.AddPublishedAsync(db, planId));
        return template!;
    }

    private async Task<Guid> DisciplineAsync(string code)
    {
        var planId = await app.RealPlanIdAsync();
        var id = Guid.Empty;
        await app.WithDbAsync(async db =>
            id = await db.Disciplines.Where(d => d.CurriculumPlanId == planId && d.Code == code).Select(d => d.Id).SingleAsync());
        return id;
    }

    private async Task<DocumentDetails> CreateAsync(HttpClient client, Guid templateId, string code)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new CreateDocumentRequest(templateId, await DisciplineAsync(code)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDetails>())!;
    }

    private static string? S(JsonElement e, string name) => e.GetProperty(name).GetString();
    private static int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();
}
