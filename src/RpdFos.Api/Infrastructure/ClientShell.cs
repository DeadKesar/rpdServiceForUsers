using Microsoft.AspNetCore.StaticFiles;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Оболочка клиента — <c>index.html</c>. Она ссылается на файлы <c>_framework</c> с хешем в имени
/// (<c>dotnet.&lt;хеш&gt;.js</c>), и после пересборки клиента хеши другие. Если браузер оставил оболочку в кэше
/// без перепроверки (по умолчанию он вправе: у файла есть дата изменения, но нет <c>Cache-Control</c>), он просит
/// файлы, которых уже нет, и страница открывается пустой. Поэтому оболочка уходит с <c>Cache-Control: no-cache</c>:
/// браузер хранит её, но каждый раз спрашивает сервер (по ETag; не изменилась — ответ 304 без тела).
/// Остальные файлы не затрагиваются: у файла с хешем новое содержимое — новое имя, а значит, другой адрес.
/// </summary>
public static class ClientShell
{
    public const string FileName = "index.html";

    /// <summary>
    /// Параметры раздачи статических файлов — и для <c>UseStaticFiles</c> (адрес <c>/index.html</c>), и для
    /// <c>MapFallbackToFile</c> (адреса <c>/</c> и страниц клиента вроде <c>/documents</c>): запасной маршрут строит свой
    /// конвейер статических файлов, настройки обычного к нему не относятся. Каждому вызову — свой экземпляр.
    /// </summary>
    public static StaticFileOptions CreateStaticFileOptions() => new() { OnPrepareResponse = PrepareResponse };

    private static void PrepareResponse(StaticFileResponseContext context)
    {
        if (string.Equals(context.File.Name, FileName, StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
}
