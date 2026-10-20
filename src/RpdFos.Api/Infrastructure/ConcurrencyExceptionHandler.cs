using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Сквозной механизм: конфликт версий при сохранении (оптимистичная блокировка, NFR-REL-04)
/// превращается в ответ 409 в формате ProblemDetails. Эндпоинтам не нужен try/catch.
/// </summary>
public sealed class ConcurrencyExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
            return false; // остальные исключения — стандартная обработка (500)

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        // Тело пишется, если клиент принимает JSON; код 409 уходит в любом случае.
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = VersionConflict()
        });
        return true;
    }

    /// <summary>
    /// Тот же ответ — для действий, которые запись с версией не меняют, но должны опираться на её актуальное
    /// состояние (новая редакция шаблона): версию клиента там сравнивают явно.
    /// </summary>
    public static ProblemDetails VersionConflict() => new()
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Конфликт версий",
        Detail = "Запись изменена в другой сессии. Обновите данные и повторите действие."
    };
}
