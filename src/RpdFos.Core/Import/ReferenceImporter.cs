using Microsoft.EntityFrameworkCore;
using RpdFos.Core.Data;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Directory;
using Parsed = DisciplineDataExtractor;

namespace RpdFos.Core.Import;

/// <summary>
/// Конвейер импорта: результат разбора исходных файлов → справочники, в одной транзакции.
/// <list type="bullet">
/// <item>Данные плана (<c>curriculum_plans</c>, дисциплины, детали по семестрам, матрица) — новая версия
/// плана на каждую загрузку.</item>
/// <item>Общие справочники (институты, кафедры, направление, профиль, ФГОС, компетенции, индикаторы,
/// сотрудники) — обновление по естественным ключам, без дублей при повторной загрузке.</item>
/// <item>Итог — в <c>parser_runs</c>: <c>succeeded</c> (нет ошибок), <c>partial</c> (план загружен, но есть
/// ошибки), <c>failed</c> (план не создан — в справочники ничего не записано).</item>
/// </list>
/// </summary>
public sealed class ReferenceImporter(AppDbContext db, TimeProvider clock)
{
    public async Task<ParserRun> ImportAsync(Guid parserRunId, Parsed.ExtractionResult result, CancellationToken ct = default)
    {
        var run = await db.ParserRuns.SingleAsync(r => r.Id == parserRunId, ct);
        var log = result.Issues.Select(ImportIssue.From).ToList();
        var files = await SourceNamesAsync(run, ct);

        try
        {
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var session = new Session(db, clock.GetUtcNow(), files, log);
                if (await session.RunAsync(run, result, ct) is { } plan)
                {
                    run.Status = log.Any(i => i.Severity == ImportIssue.Error) ? ParserRunStatuses.Partial : ParserRunStatuses.Succeeded;
                    run.ExtractedDisciplinesCount = plan.Disciplines.Count;
                    run.ExtractedCompetencesCount = session.CompetencesCount;
                    run.ExtractedEmployeesCount = session.EmployeesCount;
                    run.CreatedCurriculumPlanId = plan.Id;
                    run.ErrorsLog = ImportIssue.Serialize(log);
                    run.CompletedAt = clock.GetUtcNow();

                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return run;
                }
                // Без плана импорт не имеет смысла: откат всего, что успели добавить.
                await tx.RollbackAsync(ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Нарушение ограничения БД или ошибка конвейера: транзакция уже откачена, запуск — failed.
            log.Add(new ImportIssue(ImportIssue.Error, files.Plan, null, null,
                $"Импорт прерван: {e.GetBaseException().Message}"));
        }

        // Статус failed пишется вне откаченной транзакции, иначе он пропал бы вместе с ней.
        db.ChangeTracker.Clear();
        return await FailAsync(parserRunId, log, ct);
    }

    private async Task<ParserRun> FailAsync(Guid runId, List<ImportIssue> log, CancellationToken ct)
    {
        var run = await db.ParserRuns.SingleAsync(r => r.Id == runId, ct);
        run.Status = ParserRunStatuses.Failed;
        run.ErrorsLog = ImportIssue.Serialize(log);
        run.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task<SourceNames> SourceNamesAsync(ParserRun run, CancellationToken ct)
    {
        var ids = new[] { run.PlanFileId, run.CompListFileId, run.CompMatrixFileId, run.EmployeesFileId ?? Guid.Empty };
        var names = await db.UploadedFiles.Where(f => ids.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.OriginalName, ct);
        string Name(Guid? id) => id is Guid g && names.TryGetValue(g, out var n) ? n : "";
        return new SourceNames(Name(run.PlanFileId), Name(run.CompListFileId), Name(run.CompMatrixFileId), Name(run.EmployeesFileId));
    }

    private sealed record SourceNames(string Plan, string CompetenceList, string Matrix, string Employees);

    /// <summary>Один импорт: сопоставления «естественный ключ → строка» и журнал замечаний.</summary>
    private sealed class Session(AppDbContext db, DateTimeOffset now, SourceNames files, List<ImportIssue> log)
    {
        // Институты, кафедры по названию и сотрудники — общие правила загрузки файла должностей.
        private readonly EmployeeImport _employees = new(db, now, m => log.Add(new ImportIssue(ImportIssue.Warning, files.Employees, null, null, m)));
        private readonly Dictionary<string, Department> _departmentsByCode = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Competence> _competences = new(StringComparer.Ordinal);

        public int CompetencesCount { get; private set; }
        public int EmployeesCount { get; private set; }

        private void Error(string file, string message) => log.Add(new ImportIssue(ImportIssue.Error, file, null, null, message));
        private void Warning(string file, string message) => log.Add(new ImportIssue(ImportIssue.Warning, file, null, null, message));

        public async Task<CurriculumPlan?> RunAsync(ParserRun run, Parsed.ExtractionResult r, CancellationToken ct)
        {
            if (!CanCreatePlan(r)) return null;
            var p = r.Plan!;

            await UpsertInstitutesAsync(p, r.Employees, ct);
            await UpsertDepartmentsAsync(p, r.Employees, ct);
            var direction = await UpsertDirectionAsync(p, ct);
            var profile = await UpsertProfileAsync(direction, p.Profile!, ct);
            if (await UpsertFgosAsync(direction, p.Fgos, ct) is { } fgos)
                await UpsertCompetencesAsync(fgos, profile, r.Competences, ct);
            else if (r.Competences.Count > 0)
                Error(files.CompetenceList, "ФГОС учебного плана не определён: компетенции и матрица не загружены");
            await UpsertEmployeesAsync(r.Employees, ct);

            var plan = await CreatePlanAsync(run, direction, profile, p, r.Disciplines, ct);
            AddMatrix(plan, r.Matrix);
            AddUnmatchedRows(plan, r.UnmatchedMatrixRows ?? []);
            return plan;
        }

        private bool CanCreatePlan(Parsed.ExtractionResult r)
        {
            var p = r.Plan;
            var missing = new List<string>();
            if (p?.DirectionCode is null) missing.Add("код направления");
            if (p?.Profile is null) missing.Add("профиль");
            if (p is null || p.Level == Parsed.EducationLevel.Unknown) missing.Add("уровень образования");
            if (p is null || p.StudyForm == Parsed.StudyForm.Unknown) missing.Add("форма обучения");
            if (p?.Year is null) missing.Add("год набора");
            if (r.Disciplines.Count == 0) missing.Add("дисциплины");
            if (missing.Count == 0) return true;

            Error(files.Plan, $"Учебный план не создан: не прочитаны {string.Join(", ", missing)}");
            return false;
        }

        // ---------- Оргструктура ----------

        // Институты и сотрудники — по общим правилам загрузки файла должностей (EmployeeImport).
        private Task UpsertInstitutesAsync(Parsed.CurriculumPlanInfo p, IReadOnlyList<Parsed.Employee> employees, CancellationToken ct) =>
            _employees.UpsertInstitutesAsync(p.Institute, employees, ct);

        private async Task UpsertDepartmentsAsync(Parsed.CurriculumPlanInfo p, IReadOnlyList<Parsed.Employee> employees, CancellationToken ct)
        {
            // Кафедры — из справочника плана (там номер, на который ссылаются дисциплины); институт кафедры —
            // из файла должностей. Кафедры, которых нет в плане, не создаются: их номер неизвестен.
            // Этап 5.4 (B6): кафедра ищется по названию нестрого (DepartmentNames), номер действует внутри плана —
            // в планах разных лет номера и написание названий расходятся. Конфликт номера или сокращения с другой
            // кафедрой справочника загрузку не блокирует: справочник оставляет прежнее, план ссылается на свою кафедру.
            var all = await db.Departments.ToListAsync(ct);
            // Название кафедры меняет только план более нового года набора: план того же года — тоже «последний», и
            // планы одного года переписывали бы название друг за другом (этап 6, A7: «правовове» / «правовое»).
            var newest = p.Year > (await db.CurriculumPlans.MaxAsync(x => (int?)x.Year, ct) ?? int.MinValue);
            foreach (var d in p.Departments)
            {
                var department = all.FirstOrDefault(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))
                                 ?? all.FirstOrDefault(x => DepartmentNames.Same(x.Name, d.Name));
                var shortName = d.ShortName.Length > 0 ? d.ShortName : d.Name;
                if (department is null)
                {
                    department = new Department { Name = d.Name, Code = d.Code, ShortName = shortName };
                    db.Departments.Add(department);
                    all.Add(department);
                }
                else if (!string.Equals(department.Name, d.Name, StringComparison.OrdinalIgnoreCase))
                {
                    // Одна кафедра, разное написание: название — из плана более нового года набора.
                    var kept = newest && all.All(x => x == department || !string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))
                        ? d.Name : department.Name;
                    Warning(files.Plan, $"Кафедра «{d.Name}» — та же, что «{department.Name}» в справочнике (название различается); оставлено «{kept}»");
                    _employees.DepartmentsByName[d.Name] = department;
                    _employees.DepartmentsByName[department.Name] = department;
                    department.Name = kept;
                }

                if (department.Code != d.Code)
                {
                    if (all.Any(x => x != department && x.Code == d.Code))
                        Warning(files.Plan, $"Кафедра «{d.Name}»: номер {d.Code} в справочнике у другой кафедры — оставлен номер «{department.Code}»");
                    else department.Code = d.Code;
                }
                if (department.ShortName != shortName)
                {
                    if (all.Any(x => x != department && x.ShortName == shortName))
                        Warning(files.Plan, $"Кафедра «{d.Name}»: сокращение «{shortName}» в справочнике у другой кафедры — оставлено «{department.ShortName}»");
                    else department.ShortName = shortName;
                }
                // Новой кафедре номер или сокращение другой кафедры не достаются: уникальные — с номером плана.
                if (department.Code == d.Code && all.Any(x => x != department && x.Code == department.Code))
                    department.Code = Fit($"{d.Code}/{shortName}");
                if (all.Any(x => x != department && x.ShortName == department.ShortName))
                    department.ShortName = Fit($"{shortName}-{d.Code}");
                _employees.LinkInstitute(department, employees);

                _employees.DepartmentsByName[department.Name] = department;
                _departmentsByCode[d.Code] = department;
            }
        }
        /// <summary>
        /// Кафедра дисциплины: по номеру из листа «Кафедры» этого плана; иначе — кафедра справочника с тем же названием
        /// (нестрого, из колонки «Закрепленная кафедра» плана), а если у плана нет листа «Кафедры» — с тем же номером.
        /// </summary>
        private Department? DepartmentOf(Parsed.Discipline d, IReadOnlyList<Department> known)
        {
            if (d.DepartmentCode is not null && _departmentsByCode.TryGetValue(d.DepartmentCode, out var byPlan)) return byPlan;
            if (d.DepartmentName is { } name && known.FirstOrDefault(x => DepartmentNames.Same(x.Name, name)) is { } byName) return byName;
            return d.DepartmentCode is not null && _departmentsByCode.Count == 0
                ? known.FirstOrDefault(x => x.Code == d.DepartmentCode)
                : null;
        }

        private static string Fit(string value) => value.Length <= 50 ? value : value[..50];

        // ---------- Направление, профиль, ФГОС, компетенции ----------

        private async Task<StudyDirection> UpsertDirectionAsync(Parsed.CurriculumPlanInfo p, CancellationToken ct)
        {
            var code = p.DirectionCode!;
            var direction = await db.StudyDirections.SingleOrDefaultAsync(d => d.Code == code, ct);
            if (direction is null)
            {
                direction = new StudyDirection { Code = code };
                db.StudyDirections.Add(direction);
            }

            direction.Name = p.DirectionName ?? (direction.Name.Length > 0 ? direction.Name : code);
            direction.EducationLevel = p.Level switch
            {
                Parsed.EducationLevel.Bachelor => EducationLevels.Bachelor,
                Parsed.EducationLevel.Master => EducationLevels.Master,
                Parsed.EducationLevel.Specialist => EducationLevels.Specialist,
                _ => EducationLevels.Postgraduate
            };
            if (p.ResponsibleDepartment is { } name)
            {
                if (_employees.DepartmentsByName.TryGetValue(name, out var department)) direction.ResponsibleDepartmentId = department.Id;
                else Warning(files.Plan, $"Выпускающая кафедра «{name}» не найдена в справочнике кафедр плана");
            }
            return direction;
        }

        private async Task<StudyProfile> UpsertProfileAsync(StudyDirection direction, string name, CancellationToken ct)
        {
            var profile = await db.StudyProfiles.SingleOrDefaultAsync(x => x.DirectionId == direction.Id && x.Name == name, ct);
            if (profile is null)
            {
                profile = new StudyProfile { DirectionId = direction.Id, Name = name };
                db.StudyProfiles.Add(profile);
            }
            return profile;
        }

        private async Task<Fgos?> UpsertFgosAsync(StudyDirection direction, Parsed.FgosInfo? info, CancellationToken ct)
        {
            if (info?.ApprovedOn is not DateOnly approvedOn) return null;
            var number = info.OrderNumber;
            var fgos = await db.Fgos.SingleOrDefaultAsync(f =>
                f.DirectionId == direction.Id && f.OrderNumber == number && f.ApprovedOn == approvedOn, ct);
            if (fgos is null)
            {
                // Версия стандарта («3++») в файлах не указана — заполняется вручную.
                fgos = new Fgos { DirectionId = direction.Id, OrderNumber = number, ApprovedOn = approvedOn };
                db.Fgos.Add(fgos);
            }
            return fgos;
        }

        private async Task UpsertCompetencesAsync(
            Fgos fgos, StudyProfile profile, IReadOnlyList<Parsed.Competence> parsed, CancellationToken ct)
        {
            // УК и ОПК — общие для ФГОС, ПК — свои у профиля: ключи (fgos, код) и (профиль, код).
            var existing = (await db.Competences.Include(c => c.Indicators)
                    .Where(c => (c.FgosId == fgos.Id && c.ProfileId == null) || c.ProfileId == profile.Id)
                    .ToListAsync(ct))
                .GroupBy(c => c.Code)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.ProfileId == profile.Id).First());

            foreach (var c in parsed)
            {
                var professional = c.Category == Parsed.CompetenceCategory.Professional;
                if (!existing.TryGetValue(c.Code, out var competence) || professional != (competence.ProfileId is not null))
                {
                    competence = new Competence { Code = c.Code };
                    db.Competences.Add(competence);
                }
                competence.FgosId = fgos.Id;
                competence.ProfileId = professional ? profile.Id : null;
                competence.Category = c.Category switch
                {
                    Parsed.CompetenceCategory.Universal => CompetenceCategories.Universal,
                    Parsed.CompetenceCategory.GeneralProfessional => CompetenceCategories.GeneralProfessional,
                    _ => CompetenceCategories.Professional
                };
                competence.CategoryName = c.CategoryName;
                competence.Formulation = c.Formulation;

                foreach (var i in c.Indicators)
                {
                    var indicator = competence.Indicators.FirstOrDefault(x => x.Code == i.Code);
                    if (indicator is null)
                    {
                        indicator = new CompetenceIndicator { Code = i.Code };
                        competence.Indicators.Add(indicator);
                    }
                    indicator.Text = i.Text;
                    // Тип не распознан — индикатор одной формулировкой, тип не задан (этап 5.4, B5).
                    indicator.IndicatorType = i.Type switch
                    {
                        Parsed.IndicatorType.Knows => IndicatorTypes.Knows,
                        Parsed.IndicatorType.Can => IndicatorTypes.Can,
                        Parsed.IndicatorType.Owns => IndicatorTypes.Owns,
                        _ => null
                    };
                }
                _competences[competence.Code] = competence;
            }
            CompetencesCount = parsed.Count;
        }

        // ---------- Сотрудники ----------

        private async Task UpsertEmployeesAsync(IReadOnlyList<Parsed.Employee> parsed, CancellationToken ct)
        {
            await _employees.UpsertEmployeesAsync(parsed, ct);
            EmployeesCount = parsed.Count;
        }

        // ---------- Учебный план ----------

        private async Task<CurriculumPlan> CreatePlanAsync(
            ParserRun run, StudyDirection direction, StudyProfile profile, Parsed.CurriculumPlanInfo p,
            IReadOnlyList<Parsed.Discipline> parsed, CancellationToken ct)
        {
            var studyForm = p.StudyForm switch
            {
                Parsed.StudyForm.PartTime => StudyForms.PartTime,
                Parsed.StudyForm.Evening => StudyForms.Evening,
                _ => StudyForms.FullTime
            };
            var year = p.Year!.Value;
            var last = await db.CurriculumPlans
                .Where(x => x.DirectionId == direction.Id && x.ProfileId == profile.Id && x.Year == year && x.StudyForm == studyForm)
                .MaxAsync(x => (int?)x.Version, ct);

            var plan = new CurriculumPlan
            {
                DirectionId = direction.Id, ProfileId = profile.Id, Year = year, StudyForm = studyForm,
                Version = (last ?? 0) + 1, SourceFileId = run.PlanFileId, CreatedAt = now
            };

            // Кафедры справочника — для дисциплин плана без листа «Кафедры» (и с номером, которого на листе нет).
            var known = db.Departments.Local.ToList(); // загружены и добавлены в UpsertDepartmentsAsync
            var byCode = new Dictionary<string, Discipline>();
            foreach (var d in parsed)
            {
                var department = DepartmentOf(d, known);
                if (d.DepartmentCode is not null && department is null)
                    Warning(files.Plan, $"{d.Code}: кафедра с номером {d.DepartmentCode} не загружена — дисциплина без кафедры");

                var discipline = new Discipline
                {
                    CurriculumPlanId = plan.Id, DepartmentId = department?.Id, Code = d.Code, Name = d.Name,
                    Block = d.Block switch
                    {
                        Parsed.PlanBlock.B1 => PlanBlocks.Disciplines,
                        Parsed.PlanBlock.B2 => PlanBlocks.Practice,
                        Parsed.PlanBlock.B3 => PlanBlocks.FinalAttestation,
                        _ => PlanBlocks.Optional
                    },
                    PartType = d.Part switch
                    {
                        Parsed.PlanPart.Mandatory => PartTypes.Mandatory,
                        Parsed.PlanPart.Variable => PartTypes.Variable,
                        Parsed.PlanPart.ElectiveGroup => PartTypes.ElectiveGroup,
                        _ => PartTypes.ElectiveItem
                    },
                    EntryKind = d.Kind switch
                    {
                        Parsed.EntryKind.Discipline => EntryKinds.Discipline,
                        Parsed.EntryKind.Module => EntryKinds.Module,
                        Parsed.EntryKind.Practice => EntryKinds.Practice,
                        Parsed.EntryKind.Gia => EntryKinds.Gia,
                        _ => EntryKinds.Optional
                    },
                    TotalHours = d.TotalHours,
                    TotalCredits = d.Credits,
                    Semesters = d.Semesters.Select(s => new DisciplineSemesterDetails
                    {
                        Semester = s.Semester, ContactHours = s.ContactHours, Lectures = s.Lectures, Practice = s.Practice,
                        LabHours = s.Labs, SelfStudy = s.SelfStudy, Control = s.Control, Credits = s.Credits,
                        ControlTypes = s.ControlTypes.Select(ControlTypeCode).Distinct().ToList()
                    }).ToList()
                };
                byCode[d.Code] = discipline;
                plan.Disciplines.Add(discipline);
            }

            // Родитель — через навигацию: EF вставит родителя раньше дочерних строк.
            foreach (var d in parsed.Where(d => d.ParentCode is not null))
                byCode[d.Code].Parent = byCode[d.ParentCode!];

            db.CurriculumPlans.Add(plan);
            return plan;
        }

        private static string ControlTypeCode(Parsed.ControlType type) => type switch
        {
            Parsed.ControlType.Exam => ControlTypeCodes.Exam,
            Parsed.ControlType.Credit => ControlTypeCodes.Credit,
            Parsed.ControlType.GradedCredit => ControlTypeCodes.GradedCredit,
            Parsed.ControlType.CourseworkProject => ControlTypeCodes.CourseProject,
            _ => ControlTypeCodes.CourseWork
        };

        // Строки матрицы с отметками, не сопоставленные с дисциплиной плана, — для ручной привязки (этап 5.2):
        // иначе их связи с компетенциями терялись бы, оставаясь только замечанием.
        private void AddUnmatchedRows(CurriculumPlan plan, IReadOnlyList<Parsed.UnmatchedMatrixRow> rows)
        {
            foreach (var row in rows)
                db.MatrixUnmatchedRows.Add(new MatrixUnmatchedRow
                {
                    CurriculumPlanId = plan.Id, Sheet = row.Sheet, RowNumber = row.Row, Name = row.Name, Semesters = row.Semesters,
                    Codes = row.Codes.ToList(), CompetenceCodes = row.CompetenceCodes.ToList(), Candidates = row.Candidates.ToList()
                });
        }

        private void AddMatrix(CurriculumPlan plan, IReadOnlyList<Parsed.MatrixLink> links)
        {
            var disciplines = plan.Disciplines.ToDictionary(d => d.Code);
            var added = new HashSet<(Guid, Guid)>();
            var missing = new HashSet<string>();
            foreach (var link in links)
            {
                if (!disciplines.TryGetValue(link.DisciplineCode, out var discipline)) continue;
                if (!_competences.TryGetValue(link.CompetenceCode, out var competence))
                {
                    if (_competences.Count > 0 && missing.Add(link.CompetenceCode))
                        Error(files.Matrix, $"Компетенция {link.CompetenceCode} из матрицы не загружена — её связи пропущены");
                    continue;
                }
                // Матрица в проекте — на уровне компетенций; связи с индикаторами сводятся к компетенции.
                if (added.Add((discipline.Id, competence.Id)))
                    db.CompetenceMatrix.Add(new CompetenceMatrixEntry { DisciplineId = discipline.Id, CompetenceId = competence.Id });
            }
        }
    }
}
