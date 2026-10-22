using System.Net.Http.Headers;

namespace RpdFos.Core.Printing;

/// <summary>Преобразование DOCX в PDF тем же макетом (FR-GEN-02): PDF — из сформированного DOCX, а не отдельной вёрсткой.</summary>
public interface IPdfConverter
{
    Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct = default);
}

/// <summary>Конвертер недоступен или отказал — задача формирования завершается с этой причиной.</summary>
public sealed class PdfConversionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Конвертер — Gotenberg (LibreOffice в режиме headless) отдельным сервисом: <c>POST /forms/libreoffice/convert</c>
/// с файлом в multipart, в ответе — PDF. Адрес сервиса — у HttpClient (настройка <c>Converter:Url</c>).
/// </summary>
public sealed class GotenbergPdfConverter(HttpClient http) : IPdfConverter
{
    public async Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(docx);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        // Имя для конвертера — латиницей: по расширению он выбирает обработчик, кириллица в заголовке ему не нужна.
        content.Add(file, "files", "document.docx");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("forms/libreoffice/convert", content, ct);
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
