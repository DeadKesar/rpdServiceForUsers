using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace RpdFos.Core.Data.Entities;

/// <summary>
/// Учётная запись для входа (ASP.NET Core Identity). Пароль хранится только здесь — в виде хеша.
/// Справочник пользователей с должностями и категориями подписантов (users, раздел 2.4 ВКР)
/// появится на этапе 2 и будет ссылаться на учётную запись через identity_subject.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";

    /// <summary>
    /// Учётная запись отключена администратором — бессрочная блокировка Identity (этап 5.2, FR-ADM-01). Служебные письма
    /// (этап 8, A6) отключённой не отправляются, и ссылка из прежнего письма пароля ей не вернёт.
    /// </summary>
    [NotMapped]
    public bool IsDisabled => LockoutEnd == DateTimeOffset.MaxValue;
}
