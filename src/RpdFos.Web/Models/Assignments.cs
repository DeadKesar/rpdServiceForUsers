namespace RpdFos.Web.Models;

// Контракт API закрепления дисциплин (Features/Assignments) на стороне клиента (этап 7, A10).

public sealed record DepartmentOption(Guid Id, string Name, string ShortName);

/// <summary>Страница «Закрепление дисциплин» для плана: кафедры его дисциплин (для фильтра) и сами дисциплины.</summary>
public sealed record AssignmentPage(List<DepartmentOption> Departments, List<DisciplineRow> Disciplines);

public sealed record DisciplineRow(Guid Id, string Code, string Name, Guid? DepartmentId, string? Department,
    List<AssigneeItem> Assigned, List<DisciplineDocument> Documents);

/// <param name="Id">Идентификатор закрепления — по нему оно снимается.</param>
public sealed record AssigneeItem(Guid Id, Guid EmployeeId, string Name, DateTimeOffset AssignedAt, string AssignedBy);

public sealed record DisciplineDocument(Guid Id, string Type, string Status, string StatusTitle, bool IsOutdated, string AuthorName);

/// <summary>Итог «Закрепить»: сколько закреплений добавлено и сколько дисциплин уже были за сотрудником.</summary>
public sealed record AssignmentSummary(int Added, int AlreadyAssigned);
