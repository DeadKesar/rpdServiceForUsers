using System.Net.Http.Headers;
using System.Text.Json;

namespace RpdFos.Core.Printing;

/// <summary>Преобразование DOCX в PDF тем же макетом (FR-GEN-02): PDF — из сформированного DOCX, а не отдельной вёрсткой.</summary>
public interface IPdfConverter
{
    Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct = default);

    /// <summary>
    /// Заголовки PDF со страницами по порядку документа — закладки PDF, которые конвертер делает из уровней структуры DOCX. По ним
    /// номера страниц вписываются в оглавление DOCX (этап 8, A13).
    /// </summary>
    Task<IReadOnlyList<PdfHeading>> HeadingsAsync(byte[] pdf, CancellationToken ct = default);
}

/// <summary>Закладка PDF: текст заголовка и номер страницы с единицы.</summary>
public sealed record PdfHeading(string Title, int Page);

/// <summary>Конвертер недоступен или отказал — задача формирования завершается с этой причиной.</summary>
public sealed class PdfConversionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Конвертер — Gotenberg (LibreOffice в режиме headless) отдельным сервисом: <c>POST /forms/libreoffice/convert</c>
/// с файлом в multipart, в ответе — PDF; <c>POST /forms/pdfengines/bookmarks/read</c> — закладки PDF. Адрес сервиса — у HttpClient
/// (настройка <c>Converter:Url</c>).
/// </summary>
public sealed class GotenbergPdfConverter(HttpClient http) : IPdfConverter
{
    public Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct = default) =>
        // Имя для конвертера — латиницей: по расширению он выбирает обработчик, кириллица в заголовке ему не нужна.
        PostAsync("forms/libreoffice/convert", docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "document.docx", ct);

    public async Task<IReadOnlyList<PdfHeading>> HeadingsAsync(byte[] pdf, CancellationToken ct = default)
    {
        var json = await PostAsync("forms/pdfengines/bookmarks/read", pdf, "application/pdf", "document.pdf", ct);
        try
        {
            // Ответ: имя файла → дерево закладок; вложенность у заголовков есть, порядок документа — обход сверху вниз.
            using var document = JsonDocument.Parse(json);
            var headings = new List<PdfHeading>();
            foreach (var file in document.RootElement.EnumerateObject()) Collect(file.Value, headings);
            return headings;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new PdfConversionException($"Конвертер PDF вернул закладки в неизвестном виде: {e.Message}", e);
        }
    }

    private static void Collect(JsonElement bookmarks, List<PdfHeading> headings)
    {
        // Закладок нет — null; у закладки без вложенных children нет.
        if (bookmarks.ValueKind != JsonValueKind.Array) return;
        foreach (var bookmark in bookmarks.EnumerateArray())
        {
            headings.Add(new PdfHeading(bookmark.GetProperty("title").GetString() ?? "", bookmark.GetProperty("page").GetInt32()));
            if (bookmark.TryGetProperty("children", out var children)) Collect(children, headings);
        }
    }

    private async Task<byte[]> PostAsync(string route, byte[] bytes, string mediaType, string fileName, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Add(file, "files", fileName);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(route, content, ct);
        }
        catch (HttpRequestException e)
        {
            throw new PdfConversionException($"Конвертер PDF недоступен ({http.BaseAddress}): {e.Message}", e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new PdfConversionException(
                    $"Конвертер PDF отказал ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }
}
