namespace RpdFos.Web.Models;

// Контракт API справочника сотрудников (Features/Directory) на стороне клиента.

/// <summary>Сотрудник для выбора составителя: «Фамилия И. О.», подпись «И. О. Фамилия», должность, кафедра.</summary>
public sealed record DirectoryPerson(Guid Id, string ShortName, string NameForDocument, string? Position, string? Department);
