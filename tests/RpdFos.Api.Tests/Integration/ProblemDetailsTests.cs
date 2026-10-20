using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RpdFos.Core.Data.Entities;
using Xunit;

namespace RpdFos.Api.Tests.Integration;

/// <summary>Ответы об ошибках — по-русски: стандартные английские заголовки ProblemDetails заменяет сервер.</summary>
[Collection(DatabaseCollection.Name)]
public class ProblemDetailsTests(DatabaseFixture app)
{
    [Fact]
    public async Task Missing_template_is_explained()
    {
        var response = await app.CreateClientFor(AppRoles.Methodist).GetAsync($"/api/templates/{Guid.NewGuid()}");

        var problem = await ProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("Шаблон не найден", problem.Title);
        Assert.Contains("удалён в другой вкладке", problem.Detail);
    }

    [Theory]
    [InlineData(AppRoles.Methodist, "/api/no-such-endpoint", null, HttpStatusCode.NotFound, "Не найдено")]
    [InlineData(AppRoles.Methodist, "/api/templates", "{", HttpStatusCode.BadRequest, "Неверный запрос")]
    [InlineData(AppRoles.Teacher, "/api/templates", "{}", HttpStatusCode.Forbidden, "Недостаточно прав")]
    [InlineData(null, "/api/templates", null, HttpStatusCode.Unauthorized, "Требуется вход")]
    public async Task Default_titles_are_russian(string? role, string url, string? json, HttpStatusCode status, string title)
    {
        var client = app.CreateClientFor(role);

        var response = json is null
            ? await client.GetAsync(url)
            : await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

        var problem = await ProblemAsync(response, status);
        Assert.Equal(title, problem.Title);
        Assert.False(string.IsNullOrEmpty(problem.Detail));
    }

    private static async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
    }
}
