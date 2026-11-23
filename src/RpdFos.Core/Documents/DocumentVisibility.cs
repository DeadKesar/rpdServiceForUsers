using RpdFos.Core.Data.Entities;

namespace RpdFos.Core.Documents;

/// <summary>
/// Кто откроет документ (FR-DOC-08; этап 6, B1; этап 7, A10, B3; этап 8, A4): автор, методист, администратор, согласующий, у
/// которого в документе есть строка согласования, сотрудник, за которым закреплена дисциплина документа (решение 5), и автор
/// связанного документа: автор ФОС читает РПД, по которой тот создан, автор РПД — каждый ФОС по ней; только читают, не правя
/// (FR-DOC-19, решение 14). В API то же правило применяет
/// <c>DocumentViewer</c> — по токену, здесь оно считается по ролям учётной записи из базы: им пользуются места, где нужно
/// заранее узнать, откроет ли документ конкретный человек, и дать ему уведомление со ссылкой или без неё (напоминания о
/// доработке, этап 7, A6; передача документа, A9). Ссылка на документ, который человеку недоступен, привела бы его на 403.
/// </summary>
public static class DocumentVisibility
{
    /// <param name="roles">Роли действующей учётной записи сотрудника (<c>DirectoryAccounts.RolesOfActiveAsync</c>).</param>
    /// <param name="links">
    /// Связи документов обязательны (этап 8, A4): автор РПД читает ФОС по ней, автор ФОС — РПД, и любое место, считающее доступ
    /// без них, разошлось бы с API — ссылка в уведомлении привела бы к 403.
    /// </param>
    public static async Task<bool> CanOpenAsync(
        DocumentApproval approval, DisciplineAssignments assignments, LinkedDocuments links, Document document, Guid employeeId,
        IReadOnlyList<string> roles, CancellationToken ct = default) =>
        document.AuthorId == employeeId || roles.Contains(AppRoles.Methodist) || roles.Contains(AppRoles.Admin)
        || roles.Contains(AppRoles.Approver) && await approval.HasSeatAsync(document.Id, employeeId, ct)
        || await assignments.IsAssignedAsync(document.DisciplineId, employeeId, ct)
        || await links.IsLinkedAuthorAsync(document, employeeId, ct);
}
