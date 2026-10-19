using System.Security.Claims;
using DisciplineDataExtractor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using RpdFos.Core.Import;
using Xunit;
using Xunit.Abstractions;

namespace RpdFos.Api.Tests.Integration;

/// <summary>
/// Конвейер импорта на реальных файлах 09.04.04 (tests/fixtures). У коллекции своя база в контейнере:
/// успешный импорт — один сценарий (первая загрузка в чистые справочники, затем повторная), а тест
/// с ошибкой справочники не трогает, поэтому порядок тестов не важен. Проверяются приросты строк.
/// </summary>
[Collection(ImportCollection.Name)]
public class ImportTests(ImportDatabaseFixture app, ITestOutputHelper output)
{
    // Строк после импорта реальных файлов: справочники и данные плана.
    private static readonly Dictionary<string, int> FirstImport = new()
    {
        ["institutes"] = 4, ["departments"] = 30, ["study_directions"] = 1, ["study_profiles"] = 1, ["fgos"] = 1,
        ["competences"] = 17, ["competence_indicators"] = 57, ["users"] = 37,
        ["curriculum_plans"] = 1, ["disciplines"] = 46, ["discipline_semester_details"] = 52, ["competence_matrix"] = 97,
        ["uploaded_files"] = 4, ["parser_runs"] = 1
    };

    // Повторная загрузка тех же файлов: общие справочники не растут, план — новой версией.
    private static readonly Dictionary<string, int> RepeatImport = FirstImport.ToDictionary(x => x.Key, x => x.Key switch
    {
        "curriculum_plans" or "disciplines" or "discipline_semester_details" or "competence_matrix"
            or "uploaded_files" or "parser_runs" => x.Value,
        _ => 0
    });

    [Fact]
    public async Task Import_fills_reference_tables_and_repeat_creates_new_plan_version()
    {
        var methodist = await AddMethodistAsync();
        var before = await CountsAsync();

        var first = await ImportAsync(methodist, ReadFixtures());
        var afterFirst = await CountsAsync();
        output.WriteLine("Строк после импорта реальных файлов:");
        foreach (var (table, count) in Delta(before, afterFirst)) output.WriteLine($"  {table,-28} {count}");
        output.WriteLine("Замечания импорта:");
        foreach (var i in ImportIssue.Deserialize(first.ErrorsLog)) output.WriteLine($"  {i.Severity}: {i.File}, {i.Sheet}, {i.Row} — {i.Message}");

        // Одна ошибка разбора: в матрице есть дисциплина, которой нет в плане (связи с ПК-1, ПК-2).
        Assert.Equal(ParserRunStatuses.Partial, first.Status);
        Assert.Equal((46, 17, 37), (first.ExtractedDisciplinesCount, first.ExtractedCompetencesCount, first.ExtractedEmployeesCount));
        Assert.NotNull(first.CompletedAt);
        Assert.Contains(ImportIssue.Deserialize(first.ErrorsLog), i =>
            i.Severity == ImportIssue.Error && i.Message.Contains("Внедрение и эксплуатация информационных систем"));
        Assert.Equal(FirstImport, Delta(before, afterFirst));
        await AssertImportedRowsAsync(first);

        var second = await ImportAsync(methodist, ReadFixtures());
        var afterSecond = await CountsAsync();

        Assert.Equal(ParserRunStatuses.Partial, second.Status);
        Assert.Equal(RepeatImport, Delta(afterFirst, afterSecond));
        await WithDbAsync(async db =>
        {
            var plans = await db.CurriculumPlans
                .Where(p => p.Id == first.CreatedCurriculumPlanId || p.Id == second.CreatedCurriculumPlanId)
                .OrderBy(p => p.Version).Select(p => p.Version).ToListAsync();
            Assert.Equal(2, plans.Count);
            Assert.Equal(plans[0] + 1, plans[1]);
        });

        // Смена человека на должности: в файле должностей у заведующих ИТиВС и ПриМ другие ФИО.
        // Заведующий ПриМ заранее связан с учётной записью (например, остаётся преподавателем).
        const string itvsHead = "Информационные технологии и вычислительные системы", primHead = "Прикладная математика";
        var subject = Guid.NewGuid().ToString();
        Guid itvsBefore = default, primBefore = default;
        await WithDbAsync(async db =>
        {
            itvsBefore = (await db.Employees.SingleAsync(e => e.OfficialDesignation == itvsHead)).Id;
            var prim = await db.Employees.SingleAsync(e => e.OfficialDesignation == primHead);
            (prim.IdentitySubject, prim.Email, prim.CanLogin) = (subject, $"prim-{subject}@test.local", true);
            primBefore = prim.Id;
            await db.SaveChangesAsync();
        });
        var beforeThird = await CountsAsync();

        var third = await ImportAsync(methodist, ReadFixtures(), r => r with
        {
            Employees = r.Employees.Select(e => e.OfficialName switch
            {
                itvsHead => e with { FullName = "Преемников П. П." },
                primHead => e with { FullName = "Наследников Н. Н." },
                _ => e
            }).ToList()
        });
        var afterThird = await CountsAsync();

        Assert.Equal(ParserRunStatuses.Partial, third.Status);
        Assert.Equal(2, afterThird["users"] - beforeThird["users"]);
        await WithDbAsync(async db =>
        {
            // Прежние записи не переписаны — на них могут ссылаться прошлые согласования; снято только место.
            var itvsOld = await db.Employees.SingleAsync(e => e.Id == itvsBefore);
            Assert.Equal(("Сотрудник18 И. О.", (string?)null, (string?)null, false),
                (itvsOld.ShortName, itvsOld.OfficialDesignation, itvsOld.SignatoryRole, itvsOld.IsActive));
            var itvsNew = await db.Employees.SingleAsync(e => e.OfficialDesignation == itvsHead && e.IsActive);
            Assert.Equal(("Преемников П. П.", "П. П. Преемников", SignatoryRoles.DepartmentHead),
                (itvsNew.ShortName, itvsNew.NameForDocument, itvsNew.SignatoryRole));
            Assert.Equal(itvsOld.DepartmentId, itvsNew.DepartmentId);

            // С учётной записью — запись остаётся активной и связанной: человек не теряет доступ.
            var primOld = await db.Employees.SingleAsync(e => e.Id == primBefore);
            Assert.Equal(((string?)null, (string?)null, true, subject),
                (primOld.OfficialDesignation, primOld.SignatoryRole, primOld.IsActive, primOld.IdentitySubject));
            var primNew = await db.Employees.SingleAsync(e => e.OfficialDesignation == primHead && e.IsActive);
            Assert.Equal(("Наследников Н. Н.", SignatoryRoles.DepartmentHead), (primNew.ShortName, primNew.SignatoryRole));
        });
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DirectoryAccounts.SubjectClaim, subject)], "test"));
            Assert.Equal(primBefore, await scope.ServiceProvider.GetRequiredService<DirectoryAccounts>().FindIdAsync(user));
        }
    }

    // Проверки строк после первого импорта: кафедры, направление, ФГОС, дисциплины, матрица, подписанты.
    private async Task AssertImportedRowsAsync(ParserRun run)
    {
        await WithDbAsync(async db =>
        {
            var itvs = await db.Departments.Include(d => d.Institute).SingleAsync(d => d.ShortName == "ИТиВС");
            Assert.Equal(("9", "ИИТ"), (itvs.Code, itvs.Institute!.Code));

            var direction = await db.StudyDirections.SingleAsync(d => d.Code == "09.04.04");
            Assert.Equal((EducationLevels.Master, itvs.Id), (direction.EducationLevel, direction.ResponsibleDepartmentId));

            var fgos = await db.Fgos.SingleAsync(f => f.DirectionId == direction.Id);
            Assert.Equal(("932", new DateOnly(2017, 9, 19), (string?)null), (fgos.OrderNumber, fgos.ApprovedOn, fgos.Version));

            // Дисциплины плана: кафедра, часы и аттестация по семестрам, иерархия, матрица.
            var plan = await db.Disciplines.Include(d => d.Semesters)
                .Where(d => d.CurriculumPlanId == run.CreatedCurriculumPlanId).ToDictionaryAsync(d => d.Code);
            var modelling = plan["Б1.О.10"];
            var prim = await db.Departments.SingleAsync(d => d.ShortName == "ПриМ");
            Assert.Equal(prim.Id, modelling.DepartmentId);
            var semester = Assert.Single(modelling.Semesters);
            Assert.Equal((1, 24, 16, 8, 48, 36), (semester.Semester, semester.Lectures, semester.LabHours, semester.Practice, semester.ContactHours, semester.Control));
            Assert.Equal([ControlTypeCodes.Exam], semester.ControlTypes);
            Assert.Equal(plan["Б1.О.14"].Id, plan["Б1.О.14.01"].ParentId);
            Assert.Equal((PartTypes.ElectiveItem, plan["Б1.В.ДВ.02"].Id), (plan["Б1.В.ДВ.02.03"].PartType, plan["Б1.В.ДВ.02.03"].ParentId));

            var competences = await db.CompetenceMatrix.Where(m => m.DisciplineId == modelling.Id)
                .Join(db.Competences, m => m.CompetenceId, c => c.Id, (m, c) => c.Code).OrderBy(c => c).ToListAsync();
            Assert.Equal(["ОПК-1", "ОПК-4", "УК-1"], competences);

            // ПК — у профиля плана, УК и ОПК — общие для ФГОС.
            var profileId = await db.CurriculumPlans.Where(p => p.Id == run.CreatedCurriculumPlanId).Select(p => p.ProfileId).SingleAsync();
            var all = await db.Competences.Where(c => c.FgosId == fgos.Id).ToListAsync();
            Assert.Equal(3, all.Count(c => c.Category == CompetenceCategories.Professional && c.ProfileId == profileId));
            Assert.Equal(14, all.Count(c => c.Category != CompetenceCategories.Professional && c.ProfileId == null));

            // Подписанты: категория согласована с оргструктурой, подпись — «И. О. Фамилия».
            var head = await db.Employees.SingleAsync(e => e.OfficialDesignation == "Информационные технологии и вычислительные системы");
            Assert.Equal((SignatoryRoles.DepartmentHead, itvs.Id, (Guid?)null), (head.SignatoryRole, head.DepartmentId, head.InstituteId));
            Assert.Equal(("Сотрудник18 И. О.", "И. О. Сотрудник18", false), (head.ShortName, head.NameForDocument, head.CanLogin));

            var director = await db.Employees.SingleAsync(e => e.OfficialDesignation == "Институт информационных технологий");
            Assert.Equal((SignatoryRoles.InstituteDirector, itvs.InstituteId, (Guid?)null), (director.SignatoryRole, director.InstituteId, director.DepartmentId));

            var rector = await db.Employees.SingleAsync(e => e.OfficialDesignation == "Управление");
            Assert.Equal((SignatoryRoles.Rector, (Guid?)null, (Guid?)null), (rector.SignatoryRole, rector.DepartmentId, rector.InstituteId));

            // Кафедры нет в плане — сотрудник без кафедры и без категории, с предупреждением в журнале.
            var acting = await db.Employees.SingleAsync(e => e.OfficialDesignation == "Гидропневмоавтоматика и гидропривод");
            Assert.Equal(((string?)null, (Guid?)null), (acting.SignatoryRole, acting.DepartmentId));
            Assert.Contains(ImportIssue.Deserialize(run.ErrorsLog), i =>
                i.Severity == ImportIssue.Warning && i.Message.Contains("Гидропневмоавтоматика и гидропривод"));
        });
    }

    [Fact]
    public async Task Broken_plan_fails_and_writes_nothing()
    {
        var methodist = await AddMethodistAsync();
        var files = ReadFixtures();
        files[0] = (files[0].Name, "это не xlsx"u8.ToArray());
        var before = await CountsAsync();

        var run = await ImportAsync(methodist, files);
        var after = await CountsAsync();

        Assert.Equal(ParserRunStatuses.Failed, run.Status);
        Assert.Null(run.CreatedCurriculumPlanId);
        Assert.Contains(ImportIssue.Deserialize(run.ErrorsLog), i => i.Message.StartsWith("Учебный план не создан"));
        // Записаны только загруженные файлы и сам запуск — справочники не тронуты.
        Assert.All(Delta(before, after), x =>
            Assert.Equal(x.Key is "uploaded_files" ? 4 : x.Key is "parser_runs" ? 1 : 0, x.Value));
    }

    // ---------- Помощники ----------

    private static List<(string Name, byte[] Content)> ReadFixtures() => ImportFiles.Read();

    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<Guid> AddMethodistAsync()
    {
        var id = Guid.Empty;
        await WithDbAsync(async db => id = (await ImportFiles.AddMethodistAsync(db)).Id);
        return id;
    }

    /// <summary>Как фоновая задача импорта: файлы — в uploaded_files, запуск — в parser_runs, разбор — из базы.</summary>
    private async Task<ParserRun> ImportAsync(
        Guid methodist, IReadOnlyList<(string Name, byte[] Content)> files, Func<ExtractionResult, ExtractionResult>? change = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (run, uploaded) = await ImportFiles.AddRunAsync(db, methodist, files);

        SourceFile Source(int i) => new(uploaded[i].OriginalName, uploaded[i].Content!);
        var result = new DataExtractor().Extract(new ExtractionInput(Source(0), Source(1), Source(2), Source(3)));
        if (change is not null) result = change(result); // как если бы в файле были другие данные
        return await scope.ServiceProvider.GetRequiredService<ReferenceImporter>().ImportAsync(run.Id, result);
    }

    private async Task<Dictionary<string, int>> CountsAsync()
    {
        var counts = new Dictionary<string, int>();
        await WithDbAsync(async db =>
        {
            counts["institutes"] = await db.Institutes.CountAsync();
            counts["departments"] = await db.Departments.CountAsync();
            counts["study_directions"] = await db.StudyDirections.CountAsync();
            counts["study_profiles"] = await db.StudyProfiles.CountAsync();
            counts["fgos"] = await db.Fgos.CountAsync();
            counts["competences"] = await db.Competences.CountAsync();
            counts["competence_indicators"] = await db.CompetenceIndicators.CountAsync();
            // Справочник сотрудников без методистов, которых тесты создают сами.
            counts["users"] = await db.Employees.CountAsync(e => !e.CanLogin);
            counts["curriculum_plans"] = await db.CurriculumPlans.CountAsync();
            counts["disciplines"] = await db.Disciplines.CountAsync();
            counts["discipline_semester_details"] = await db.DisciplineSemesterDetails.CountAsync();
            counts["competence_matrix"] = await db.CompetenceMatrix.CountAsync();
            counts["uploaded_files"] = await db.UploadedFiles.CountAsync();
            counts["parser_runs"] = await db.ParserRuns.CountAsync();
        });
        return counts;
    }

    private static Dictionary<string, int> Delta(Dictionary<string, int> before, Dictionary<string, int> after) =>
        after.ToDictionary(x => x.Key, x => x.Value - before[x.Key]);
}
