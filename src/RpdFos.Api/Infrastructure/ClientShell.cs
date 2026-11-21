using Microsoft.AspNetCore.StaticFiles;

namespace RpdFos.Api.Infrastructure;

/// <summary>
/// Оболочка клиента — <c>index.html</c>. Она ссылается на файлы <c>_framework</c> с хешем в имени
/// (<c>dotnet.&lt;хеш&gt;.js</c>), и после пересборки клиента хеши другие. Если браузер оставил оболочку в кэше
/// без перепроверки (по умолчанию он вправе: у файла есть дата изменения, но нет <c>Cache-Control</c>), он просит
/// файлы, которых уже нет, и страница открывается пустой. Поэтому оболочка уходит с <c>Cache-Control: no-cache</c>:
/// браузер хранит её, но каждый раз спрашивает сервер (по ETag; не изменилась — ответ 304 без тела).
/// <para>
/// Так же отдаются два файла стилей без хеша в имени: <c>RpdFos.Web.styles.css</c> (стили компонентов) и
/// <c>css/app.css</c>. Иначе после пересборки браузер берёт прежние стили из кэша, и страница открывается без оформления
/// (этап 7, A1). Туда же — два скрипта таблиц редактора текста
/// (<c>js/rpdfos-table-logic.js</c>, <c>js/rpdfos-tables.js</c>, этап 7, B4): хост раздаёт файлы клиента обычными
/// статическими файлами, адреса с хешем в имени он не знает, а старый скрипт рядом с новой страницей ломает редактор.
/// </para>
/// Остальные файлы не затрагиваются: у файла с хешем новое содержимое — новое имя, а значит, другой адрес.
/// </summary>
public static class ClientShell
{
    public const string FileName = "index.html";

    // Адреса файлов без хеша в имени (клиент подключает их так в index.html). Сравнение по адресу, а не по имени файла:
    // «app.css» в другой папке — не наш файл.
    private static readonly PathString[] Uncached =
        ["/RpdFos.Web.styles.css", "/css/app.css", "/js/rpdfos-table-logic.js", "/js/rpdfos-tables.js"];

    /// <summary>
    /// Параметры раздачи статических файлов — и для <c>UseStaticFiles</c> (адрес <c>/index.html</c>), и для
    /// <c>MapFallbackToFile</c> (адреса <c>/</c> и страниц клиента вроде <c>/documents</c>): запасной маршрут строит свой
    /// конвейер статических файлов, настройки обычного к нему не относятся. Каждому вызову — свой экземпляр.
    /// </summary>
    public static StaticFileOptions CreateStaticFileOptions() => new() { OnPrepareResponse = PrepareResponse };

    private static void PrepareResponse(StaticFileResponseContext context)
    {
        // Оболочку узнаём по файлу: запасной маршрут отдаёт её по любому адресу страницы; остальные — по адресу запроса.
        var isShell = string.Equals(context.File.Name, FileName, StringComparison.OrdinalIgnoreCase);
        if (isShell || Uncached.Any(file => context.Context.Request.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
}
