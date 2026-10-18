namespace DisciplineDataExtractor;

/// <summary>
/// Сотрудник из файла должностей.
/// <see cref="OfficialName"/> — колонка «Офиц. название» (кафедра, институт или подразделение),
/// <see cref="DocumentTitle"/> — «То, что вносится в документ», <see cref="Department"/> — кафедра
/// для заведующих, <see cref="Institute"/> — институт (null для руководства вуза).
/// </summary>
public sealed record Employee(
    string FullName,
    string? Position,
    string? DocumentTitle,
    string OfficialName,
    string? Department,
    string? Institute);
