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
        private readonly Dictionary<string, Institute> _institutes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Department> _departmentsByName = new(StringComparer.OrdinalIgnoreCase);
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

        private async Task UpsertInstitutesAsync(Parsed.CurriculumPlanInfo p, IReadOnlyList<Parsed.Employee> employees, CancellationToken ct)
        {
            // Институты: из титула плана и файла должностей. Сокращение («ИИТ») — из строки директора:
            // её официальное название и есть институт, а «для документа» — сокращение.
            var abbreviations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            void Note(string? name, string? abbreviation)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                abbreviations[name] = abbreviation ?? abbreviations.GetValueOrDefault(name);
            }
            Note(p.Institute, null);
            foreach (var e in employees)
                Note(e.Institute, e.Department is null && e.OfficialName == e.Institute ? e.DocumentTitle : null);
            if (abbreviations.Count == 0) return;

            var names = abbreviations.Keys.ToList();
            var existing = await db.Institutes.Where(i => names.Contains(i.Name)).ToListAsync(ct);
            var usedCodes = (await db.Institutes.Select(i => i.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, abbreviation) in abbreviations)
            {
                var institute = existing.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                if (institute is null)
                {
                    var code = abbreviation is not null && usedCodes.Add(abbreviation) ? abbreviation : name;
                    if (code == name)
                        Warning(files.Employees, $"Институт «{name}»: сокращение неизвестно — код временно равен названию");
                    institute = new Institute { Code = code, Name = name, ShortName = abbreviation };
                    db.Institutes.Add(institute);
                }
                else if (abbreviation is not null)
                {
                    institute.ShortName = abbreviation;
                    // Код, равный названию, — временный (сокращения не было); настоящий код не трогаем.
                    if (institute.Code == institute.Name && usedCodes.Add(abbreviation)) institute.Code = abbreviation;
                }
                _institutes[name] = institute;
            }
        }

        private async Task UpsertDepartmentsAsync(Parsed.CurriculumPlanInfo p, IReadOnlyList<Parsed.Employee> employees, CancellationToken ct)
        {
            // Кафедры — из справочника плана (там номер, на который ссылаются дисциплины); институт кафедры —
            // из файла должностей. Кафедры, которых нет в плане, не создаются: их номер неизвестен.
            var instituteOf = employees
                .Where(e => e.Department is not null && e.Institute is not null)
                .GroupBy(e => e.Department!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Institute!, StringComparer.OrdinalIgnoreCase);

            var all = await db.Departments.ToListAsync(ct);
            foreach (var d in p.Departments)
            {
                var department = all.FirstOrDefault(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                var shortName = d.ShortName.Length > 0 ? d.ShortName : d.Name;
                var conflict = all.FirstOrDefault(x => x != department && (x.Code == d.Code || x.ShortName == shortName));
                if (conflict is not null)
                {
                    Error(files.Plan, $"Кафедра «{d.Name}»: номер {d.Code} или сокращение «{shortName}» уже у кафедры «{conflict.Name}» — не загружена");
                    continue;
                }

                if (department is null)
                {
                    department = new Department { Name = d.Name };
                    db.Departments.Add(department);
                    all.Add(department);
                }
                department.Code = d.Code;
                department.ShortName = shortName;
                if (instituteOf.TryGetValue(d.Name, out var instituteName) && _institutes.TryGetValue(instituteName, out var institute))
                    department.InstituteId = institute.Id;

                _departmentsByName[department.Name] = department;
                _departmentsByCode[department.Code] = department;
            }
        }

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
                if (_departmentsByName.TryGetValue(name, out var department)) direction.ResponsibleDepartmentId = department.Id;
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
                    if (i.Type is not Parsed.IndicatorType type)
                    {
                        Error(files.CompetenceList, $"Индикатор {i.Code}: тип не распознан — не загружен");
                        continue;
                    }
                    var indicator = competence.Indicators.FirstOrDefault(x => x.Code == i.Code);
                    if (indicator is null)
                    {
                        indicator = new CompetenceIndicator { Code = i.Code };
                        competence.Indicators.Add(indicator);
                    }
                    indicator.Text = i.Text;
                    indicator.IndicatorType = type switch
                    {
                        Parsed.IndicatorType.Knows => IndicatorTypes.Knows,
                        Parsed.IndicatorType.Can => IndicatorTypes.Can,
                        _ => IndicatorTypes.Owns
                    };
                }
                _competences[competence.Code] = competence;
            }
            CompetencesCount = parsed.Count;
        }

        // ---------- Сотрудники ----------

        private async Task UpsertEmployeesAsync(IReadOnlyList<Parsed.Employee> parsed, CancellationToken ct)
        {
            if (parsed.Count == 0) return;
            var designations = parsed.Select(e => e.OfficialName).ToList();
            // Ключ — официальное название (должность) среди активных записей.
            var existing = await db.Employees
                .Where(e => e.IsActive && e.OfficialDesignation != null && designations.Contains(e.OfficialDesignation))
                .ToDictionaryAsync(e => e.OfficialDesignation!, ct);

            // На должности другой человек: с прежней записи снимается место (должность и категория подписанта),
            // а не переписывается человек — на запись ссылаются прошлые согласования (document_signatures.resolved_user_id).
            // Справочная запись без учётной записи больше не нужна — неактивна; с учётной записью остаётся
            // активной: бывший заведующий, оставшийся преподавателем, не должен потерять доступ.
            var replaced = false;
            foreach (var e in parsed)
            {
                if (!existing.TryGetValue(e.OfficialName, out var previous) || previous.ShortName == e.FullName) continue;
                previous.OfficialDesignation = null;
                previous.SignatoryRole = null;
                if (previous.IdentitySubject is null) previous.IsActive = false;
                previous.UpdatedAt = now;
                existing.Remove(e.OfficialName);
                replaced = true;
                Warning(files.Employees, $"«{e.OfficialName}»: на должности новый сотрудник — «{previous.ShortName}» снят с должности"
                    + (previous.IsActive ? " (учётная запись сохранена)" : ", запись неактивна"));
            }
            // Уникальные индексы должности и места подписанта: прежнюю запись освободить до вставки новой.
            if (replaced) await db.SaveChangesAsync(ct);

            // Занятые места подписантов: заведующий — один на кафедру, директор — один на институт.
            var taken = (await db.Employees.Where(e => e.IsActive && e.SignatoryRole != null)
                    .Select(e => new { e.OfficialDesignation, e.SignatoryRole, e.DepartmentId, e.InstituteId }).ToListAsync(ct))
                .Where(x => x.OfficialDesignation is null || !designations.Contains(x.OfficialDesignation))
                .Select(x => SeatOf(x.SignatoryRole, x.DepartmentId, x.InstituteId))
                .OfType<string>().ToHashSet();

            foreach (var e in parsed)
            {
                Department? department = null;
                if (e.Department is not null && !_departmentsByName.TryGetValue(e.Department, out department))
                    Warning(files.Employees, $"«{e.OfficialName}»: кафедры нет в справочнике кафедр плана — сотрудник загружен без кафедры");
                var isInstituteRow = e.Department is null && e.Institute is not null && e.OfficialName == e.Institute;
                var institute = isInstituteRow ? _institutes.GetValueOrDefault(e.Institute!) : null;

                var role = EmployeeRules.SignatoryRoleOf(e.Position, e.OfficialName, isInstituteRow);
                // Категория подписанта должна быть согласована с оргструктурой (ограничение ck_users_signatory_scope).
                if ((role is SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing && department is null) ||
                    (role is SignatoryRoles.InstituteDirector && institute is null))
                {
                    Warning(files.Employees, $"«{e.OfficialName}»: подразделение не найдено — категория подписанта не назначена");
                    role = null;
                }
                var departmentId = department?.Id;
                var instituteId = department is null ? institute?.Id : null;
                if (role is not null && SignatoryRoles.University.Contains(role)) (departmentId, instituteId) = (null, null);
                if (SeatOf(role, departmentId, instituteId) is { } seat && !taken.Add(seat))
                {
                    Warning(files.Employees, $"«{e.OfficialName}»: место подписанта уже занято другим сотрудником — категория не назначена");
                    role = null;
                }

                var nameForDocument = PersonNames.NameForDocument(e.FullName);
                if (nameForDocument is null)
                    Warning(files.Employees, $"«{e.OfficialName}»: ФИО «{e.FullName}» не в формате «Фамилия И. О.» — подпись не переставлена");

                if (!existing.TryGetValue(e.OfficialName, out var employee))
                {
                    // Справочная запись: входа нет, пока Администратор не свяжет её с учётной записью.
                    employee = new Employee { OfficialDesignation = e.OfficialName, CanLogin = false, CreatedAt = now };
                    db.Employees.Add(employee);
                    existing[e.OfficialName] = employee;
                }
                employee.ShortName = e.FullName;
                employee.NameForDocument = nameForDocument ?? e.FullName;
                employee.Position = e.Position;
                employee.SignatoryRole = role;
                employee.DepartmentId = departmentId;
                employee.InstituteId = instituteId;
                employee.UpdatedAt = now;
            }
            EmployeesCount = parsed.Count;
        }

        private static string? SeatOf(string? role, Guid? departmentId, Guid? instituteId) => role switch
        {
            SignatoryRoles.DepartmentHead or SignatoryRoles.DepartmentHeadActing when departmentId is not null => $"{role}:{departmentId}",
            SignatoryRoles.InstituteDirector => $"{role}:{instituteId}",
            _ => null
        };

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

            var byCode = new Dictionary<string, Discipline>();
            foreach (var d in parsed)
            {
                Department? department = null;
                if (d.DepartmentCode is not null && !_departmentsByCode.TryGetValue(d.DepartmentCode, out department))
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
