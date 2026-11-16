namespace RpdFos.Web;

/// <summary>
/// Обрыв связи с сервером, пока страница открыта (этап 7, A1): сервер остановлен или пропала сеть. Страница сообщает
/// об этом текстом, а не необработанным исключением — Blazor на него показывает полосу «Произошла непредвиденная
/// ошибка» и пишет в консоль <c>crit</c>.
/// </summary>
public static class ConnectionLoss
{
    public const string Message = "Нет связи с сервером. Повторите позже.";

    /// <summary>
    /// Запрос не получил ответа. У <see cref="HttpRequestException"/> без кода статуса ответа нет вовсе (связь оборвана);
    /// с кодом — сервер ответил ошибкой (<c>GetFromJsonAsync</c> бросает его и на 404, и на 500): «нет связи» было бы
    /// неправдой, и такие ответы остаются за страницей.
    /// </summary>
    public static bool Is(HttpRequestException e) => e.StatusCode is null;
}
