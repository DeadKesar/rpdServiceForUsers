using Microsoft.AspNetCore.WebUtilities;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Русские заголовки ответов об ошибках. Если эндпоинт не задал заголовок, ASP.NET Core подставляет стандартный
/// английский по коду ответа («Not Found», «Bad Request») — в том числе для ответов без тела (UseStatusCodePages)
/// и необработанных исключений. Здесь такие заголовки заменяются русскими; заданные эндпоинтом не трогаются.
/// Перевод — на сервере: ProblemDetails — контракт API, и русский текст получает любой клиент.
/// </summary>
public static class ProblemTitles
{
    private static readonly Dictionary<int, (string Title, string? Detail)> Russian = new()
    {
        [StatusCodes.Status400BadRequest] =
            ("Неверный запрос", "Сервер не смог разобрать запрос. Обновите страницу и повторите действие."),
        [StatusCodes.Status401Unauthorized] = ("Требуется вход", "Сеанс истёк или вход не выполнен — войдите снова."),
        [StatusCodes.Status403Forbidden] = ("Недостаточно прав", "Это действие недоступно для вашей роли."),
        [StatusCodes.Status404NotFound] = ("Не найдено", "Запись не найдена — возможно, она удалена в другой вкладке."),
        [StatusCodes.Status405MethodNotAllowed] = ("Действие не поддерживается", null),
        [StatusCodes.Status409Conflict] = ("Конфликт", null),
        [StatusCodes.Status413PayloadTooLarge] = ("Слишком большой запрос", "Уменьшите размер загружаемого файла."),
        [StatusCodes.Status415UnsupportedMediaType] = ("Неподдерживаемый формат данных", null),
        [StatusCodes.Status422UnprocessableEntity] = ("Неверные данные", null),
        [StatusCodes.Status500InternalServerError] =
            ("Внутренняя ошибка сервера", "Повторите действие позже. Если ошибка повторится — сообщите администратору.")
    };

    // Стандартные английские заголовки: фраза ответа HTTP, общий заголовок ошибок проверки и ошибки сервера.
    private static readonly HashSet<string> Defaults =
        ["One or more validation errors occurred.", "An error occurred while processing your request."];

    public static void Apply(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        if (problem.Status is not int status || !Russian.TryGetValue(status, out var russian)) return;
        if (problem.Title is not null && problem.Title != ReasonPhrases.GetReasonPhrase(status) && !Defaults.Contains(problem.Title))
            return;

        problem.Title = russian.Title;
        problem.Detail ??= russian.Detail;
    }
}
