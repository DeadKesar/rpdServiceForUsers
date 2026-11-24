using System.Net;
using System.Text;
using RpdFos.Core.Printing;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Обращения к конвертеру PDF (Gotenberg): маршруты, разбор закладок PDF и причины отказа — без самого сервиса.</summary>
public class GotenbergPdfConverterTests
{
    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var part = ((MultipartFormDataContent)request.Content!).Single();
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {part.Headers.ContentDisposition!.FileName} {await part.ReadAsStringAsync(ct)}");
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Подключение не установлено");
    }

    private static (GotenbergPdfConverter Converter, Answer Handler) Converter(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Answer(status, body);
        return (new GotenbergPdfConverter(new HttpClient(handler) { BaseAddress = new Uri("http://converter.test/") }), handler);
    }

    private static readonly byte[] File = Encoding.ASCII.GetBytes("файл");

    [Fact]
    public async Task Docx_is_posted_to_the_conversion_route()
    {
        var (converter, handler) = Converter("%PDF");

        Assert.Equal("%PDF", Encoding.ASCII.GetString(await converter.ConvertAsync(Encoding.ASCII.GetBytes("docx"))));
        Assert.Equal(["POST /forms/libreoffice/convert document.docx docx"], handler.Requests);
    }

    [Fact]
    public async Task Nested_bookmarks_come_in_document_order()
    {
        var (converter, handler) = Converter("""
            {"document.pdf":[
              {"title":"1. Введение","page":3},
              {"title":"2. Разделы","page":3,"children":[{"title":"2.1 Первый","page":4},{"title":"2.2 Второй","page":5}]},
              {"title":"3. Итог","page":6}]}
            """);

        var headings = await converter.HeadingsAsync(Encoding.ASCII.GetBytes("pdf"));

        Assert.Equal([("1. Введение", 3), ("2. Разделы", 3), ("2.1 Первый", 4), ("2.2 Второй", 5), ("3. Итог", 6)], headings.Select(h => (h.Title, h.Page)));
        Assert.Equal(["POST /forms/pdfengines/bookmarks/read document.pdf pdf"], handler.Requests);
    }

    [Theory]
    [InlineData("""{"document.pdf":null}""")]
    [InlineData("""{"document.pdf":[]}""")]
    public async Task Pdf_without_bookmarks_has_no_headings(string body) => Assert.Empty(await Converter(body).Converter.HeadingsAsync(File));

    [Theory]
    [InlineData("не JSON")]
    [InlineData("[]")]
    [InlineData("""{"document.pdf":[{"title":"Без страницы"}]}""")]
    [InlineData("""{"document.pdf":[{"title":"Страница не число","page":"третья"}]}""")]
    public async Task Unexpected_answer_is_a_refusal(string body)
    {
        var e = await Assert.ThrowsAsync<PdfConversionException>(() => Converter(body).Converter.HeadingsAsync(File));

        Assert.Contains("закладки в неизвестном виде", e.Message);
    }

    [Fact]
    public async Task Refusal_and_unreachable_service_are_reasons()
    {
        var refused = await Assert.ThrowsAsync<PdfConversionException>(() =>
            Converter("нет такого маршрута", HttpStatusCode.NotFound).Converter.HeadingsAsync(File));
        var unreachable = await Assert.ThrowsAsync<PdfConversionException>(() =>
            new GotenbergPdfConverter(new HttpClient(new Unreachable()) { BaseAddress = new Uri("http://converter.test/") }).ConvertAsync(File));

        Assert.Equal("Конвертер PDF отказал (404): нет такого маршрута", refused.Message);
        Assert.StartsWith("Конвертер PDF недоступен (http://converter.test/)", unreachable.Message);
    }
}
