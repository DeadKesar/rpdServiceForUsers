using System.Security.Claims;
using RpdFos.Core.Data.Entities;
using RpdFos.Core.Documents;

namespace RpdFos.Api.Features.Documents;

/// <summary>
/// Кем пользователь приходится документу — одно правило для срезов «Документы», «Печать» и «Согласование».
/// Документ видят автор, методист, администратор и согласующий, у которого в документе есть строка согласования
/// (этап 6, B1): роль «Утверждающий» без назначения доступа не даёт.
/// </summary>
/// <param name="Id">Запись справочника сотрудников пользователя; null — учётная запись с ней не связана.</param>
/// <param name="IsCorrector">Методист или администратор: видят все документы, исправляют поля из плана, назначают подписантов.</param>
public sealed record DocumentViewer(Guid? Id, bool IsAuthor, bool IsReviewer, bool IsCorrector, bool IsSignatory)
{
    public bool Sees => IsAuthor || IsCorrector || IsSignatory;

    public static async Task<DocumentViewer> OfAsync(
        ClaimsPrincipal user, Guid? me, Document document, DocumentApproval approval, CancellationToken ct) => new(
        me,
        me is not null && document.AuthorId == me,
        user.IsInRole(AppRoles.Methodist),
        user.IsInRole(AppRoles.Methodist) || user.IsInRole(AppRoles.Admin),
        user.IsInRole(AppRoles.Approver) && me is Guid id && await approval.HasSeatAsync(document.Id, id, ct));
}
