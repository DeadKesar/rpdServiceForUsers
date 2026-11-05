using DisciplineDataExtractor.Internal;

namespace DisciplineDataExtractor;

/// <summary>
/// Разбор исходных данных импорта: учебный план, список и матрица компетенций, должности.
/// Статического состояния нет: на каждый вызов <see cref="Extract"/> создаются свои читатели
/// и журнал замечаний, поэтому параллельные разборы безопасны. Исключения не выбрасываются:
/// файл, который не удалось прочитать, даёт ошибку в <see cref="ExtractionResult.Issues"/>,
/// остальные файлы разбираются дальше.
/// </summary>
public sealed class DataExtractor
{
    public ExtractionResult Extract(ExtractionInput input)
    {
        var log = new IssueLog();

        var (plan, disciplines) = Safe(input.Plan, log,
            () => new PlanReader(input.Plan, log).Read(), (null, []));
        var (listDirection, competences) = Safe(input.CompetenceList, log,
            () => new CompetenceListReader(input.CompetenceList, log).Read(), (null, []));
        var (matrixDirection, matrixRows) = Safe(input.CompetenceMatrix, log,
            () => new CompetenceMatrixReader(input.CompetenceMatrix, log).Read(), (null, []));
        IReadOnlyList<Employee> employees = input.Employees is { } employeesFile
            ? Safe(employeesFile, log, () => new EmployeesReader(employeesFile, log).Read(), [])
            : [];

        // Файлы одного импорта должны относиться к одному направлению: иначе загружен не тот файл.
        if (plan?.DirectionCode is { } direction)
        {
            CheckDirection(input.CompetenceList, listDirection, direction, log);
            CheckDirection(input.CompetenceMatrix, matrixDirection, direction, log);
        }

        var (matrix, unmatched) = MatrixResolver.Resolve(input.CompetenceMatrix.FileName, matrixRows, disciplines, competences, log);
        return new ExtractionResult(plan, disciplines, competences, matrix, employees, log.Items, unmatched);
    }

    /// <summary>
    /// Только файл должностей — для загрузки справочника сотрудников без учебного плана (страница администратора,
    /// этап 5.2 прототипа). Исключений нет: нечитаемый файл — ошибка в замечаниях.
    /// </summary>
    public EmployeesResult ExtractEmployees(SourceFile employees)
    {
        var log = new IssueLog();
        IReadOnlyList<Employee> rows = Safe(employees, log, () => new EmployeesReader(employees, log).Read(), []);
        return new EmployeesResult(rows, log.Items);
    }

    private static void CheckDirection(SourceFile file, string? found, string expected, IssueLog log)
    {
        if (found is not null && found != expected)
            log.Error(file.FileName, null, null, $"Направление {found} не совпадает с направлением учебного плана {expected}");
    }

    private static T Safe<T>(SourceFile file, IssueLog log, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            // Повреждённый или не тот файл (не .xlsx/.docx) — ошибка разбора, а не падение импорта.
            log.Error(file.FileName, null, null, $"Файл не прочитан: {e.Message}");
            return fallback;
        }
    }
}
