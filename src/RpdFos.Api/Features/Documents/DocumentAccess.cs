using System.Security.Claims;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.Documents;

/// <summary>
/// Кем пользователь приходится документу — одно правило для срезов «Документы», «Печать» и «Согласование».
/// Документ видят автор, методист, администратор и согласующий, у которого в документе есть строка согласования
/// (этап 6, B1): роль «Утверждающий» без назначения доступа не даёт. Закреплённый за дисциплиной документа видит его тоже
/// (этап 7, A10, решение 5), но только для чтения: правят и действуют роль и авторство, а не закрепление. Так же читает связанную
/// РПД автор ФОС, созданного по ней (этап 7, B3; FR-DOC-19).
/// </summary>
/// <param name="Id">Запись справочника сотрудников пользователя; null — учётная запись с ней не связана.</param>
/// <param name="IsCorrector">Методист или администратор: видят все документы, исправляют поля из плана, назначают подписантов.</param>
/// <param name="IsAdmin">Администратор: принудительно меняет статус документа (этап 7, A8).</param>
/// <param name="IsAssigned">
/// Дисциплина документа закреплена за пользователем (этап 7, A10). Заполняет <c>VisibleDocumentAsync</c> — единственное место,
/// где от закрепления зависит доступ: печать и согласование его не учитывают.
/// </param>
/// <param name="IsFosAuthor">
/// Пользователь — автор ФОС, созданного по этой РПД (этап 7, B3): РПД открыта ему только для чтения. Заполняет
/// <c>VisibleDocumentAsync</c>, как и <paramref name="IsAssigned"/>.
/// </param>
public sealed record DocumentViewer(
    Guid? Id, bool IsAuthor, bool IsReviewer, bool IsCorrector, bool IsSignatory, bool IsAdmin = false, bool IsAssigned = false,
    bool IsFosAuthor = false)
{
    public bool Sees => IsAuthor || IsCorrector || IsSignatory || IsAssigned || IsFosAuthor;

    public static async Task<DocumentViewer> OfAsync(
        ClaimsPrincipal user, Guid? me, Document document, DocumentApproval approval, CancellationToken ct) => new(
        me,
        me is not null && document.AuthorId == me,
        user.IsInRole(AppRoles.Methodist),
        user.IsInRole(AppRoles.Methodist) || user.IsInRole(AppRoles.Admin),
        user.IsInRole(AppRoles.Approver) && me is Guid id && await approval.HasSeatAsync(document.Id, id, ct),
        user.IsInRole(AppRoles.Admin));
}
