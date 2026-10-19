using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RpdFos.Api.Pages.Account;

/// <summary>
/// Страница отказа в доступе: сюда cookie входа отправляет вошедшего пользователя без нужной роли
/// (например, не администратора — с панели фоновых задач). Без неё он видел «Страница не найдена».
/// </summary>
public class AccessDeniedModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
