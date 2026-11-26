using RpdFos.Manuals;
using Xunit;

namespace RpdFos.Api.Tests;

/// <summary>
/// Руководства (этап 8, B9): ссылки, якоря и снимки docs/manuals разрешаются; проверка находит то, что ломает справку «?» и PDF.
/// Сам PDF собирает конвертер: dotnet run --project tools/RpdFos.Manuals.
/// </summary>
public sealed class ManualsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("manuals").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static string Manuals()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RpdFos.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("Корень репозитория не найден."), "docs", "manuals");
    }

    private void Image(string name)
    {
        Directory.CreateDirectory(Path.Combine(folder, "img"));
        File.WriteAllBytes(Path.Combine(folder, "img", name), [1]);
    }

    private string Write(string text, string name = "a.md")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Manuals_of_the_project_have_no_broken_links_anchors_and_images() => Assert.Empty(Manual.Check(Manuals()));

    // Разделы, без которых руководство преподавателя не отвечает заданию; на них же поведёт «?» (B12).
    [Theory]
    [InlineData("login")]
    [InlineData("profile")]
    [InlineData("documents")]
    [InlineData("create")]
    [InlineData("editor")]
    [InlineData("rpd-sections")]
    [InlineData("fos-sections")]
    [InlineData("check")]
    [InlineData("submit")]
    [InlineData("print")]
    [InlineData("archive")]
    [InlineData("reading")]
    [InlineData("approver")]
    [InlineData("approver-queue")]
    public void Teacher_manual_has_section(string anchor) =>
        Assert.Contains(Manual.Load(Path.Combine(Manuals(), "teacher.md")).Headings, h => h.Anchor == anchor);

    // Разделы руководства методиста (B10): страницы роли и действия с документом; на них поведёт «?» (B12).
    [Theory]
    [InlineData("import")]
    [InlineData("import-messages")]
    [InlineData("matrix-rows")]
    [InlineData("assignments")]
    [InlineData("templates")]
    [InlineData("template-bookmarks")]
    [InlineData("template-defaults")]
    [InlineData("template-revision")]
    [InlineData("review")]
    [InlineData("comments")]
    [InlineData("plan-fields")]
    [InlineData("cross-check")]
    [InlineData("accept-document")]
    [InlineData("assign-approvers")]
    [InlineData("history")]
    [InlineData("correction")]
    [InlineData("archive")]
    [InlineData("transfer")]
    public void Methodist_manual_has_section(string anchor) =>
        Assert.Contains(Manual.Load(Path.Combine(Manuals(), "methodist.md")).Headings, h => h.Anchor == anchor);

    [Fact]
    public void Correct_manual_passes()
    {
        Image("x.png");
        Write("# A\n\n<a id=\"one\"></a>\n## Раз\n\nСм. [два](b.md#two), [раз](#one).\n\n![Снимок](img/x.png)\n");
        Write("# B\n\n<a id=\"two\"></a>\n\n## Два\n\n[Назад](a.md), [сайт](https://example.org/#x)\n", "b.md");
        Assert.Empty(Manual.Check(folder));
    }

    [Theory]
    [InlineData("# A\n\n## Раз\n", "у заголовка «Раз» нет якоря")]
    [InlineData("# A\n\n<a id=\"one\"></a>\n## Раз\n\n<a id=\"one\"></a>\n### Два\n", "якорь «one» повторяется")]
    [InlineData("# A\n\n<a id=\"Раз\"></a>\n## Раз\n", "якорь «Раз» — не строчная латиница")]
    [InlineData("# A\n\n<a id=\"one\"></a>\n\nТекст\n", "якорь «one» стоит не перед заголовком")]
    [InlineData("# A\n\n<a id=\"one\"></a>\n\n<a id=\"two\"></a>\n## Раз\n", "якорь «one» стоит не перед заголовком")]
    [InlineData("# A\n\nТекст\n\n<a id=\"one\"></a>\n", "якорь «one» стоит не перед заголовком")]
    [InlineData("# A\n\n[x](#nope)\n", "ссылка «#nope» — нет такого якоря")]
    [InlineData("# A\n\n[x](b.md#two)\n", "ссылка «b.md#two» — нет такого файла")]
    [InlineData("# A\n\n![Снимок](img/no.png)\n", "нет снимка img/no.png")]
    [InlineData("# A\n\n![](img/x.png)\n", "у снимка img/x.png нет подписи")]
    [InlineData("# A\n\nТекст ![Снимок](img/x.png)\n", "снимок img/x.png стоит внутри текста")]
    [InlineData("# A\n\n#### Глубоко\n", "заголовок глубже третьего уровня")]
    [InlineData("Без заголовка\n", "нет заголовка руководства")]
    public void Check_names_the_problem(string text, string problem)
    {
        Image("x.png");
        Write(text);
        Assert.Contains(Manual.Check(folder), p => p.StartsWith("a.md:") && p.Contains(problem));
    }

    [Fact]
    public void Check_names_an_image_nobody_shows()
    {
        Image("x.png");
        Write("# A\n");
        Assert.Equal("img/x.png: снимка нет ни в одном руководстве.", Assert.Single(Manual.Check(folder)));
    }

    [Fact]
    public void Sections_and_figures_are_numbered_and_keep_anchors()
    {
        Image("x.png");
        var manual = Manual.Load(Write(
            "# A\n\n<a id=\"one\"></a>\n## Раз\n\n![Окно <1>](img/x.png)\n\n<a id=\"sub\"></a>\n### Под\n\n<a id=\"two\"></a>\n## Два\n\n![Ещё](img/x.png)\n"));

        Assert.Equal([(2, "1. Раз", "one"), (3, "1.1. Под", "sub"), (2, "2. Два", "two")],
            manual.Headings.Select(h => (h.Level, h.Text, h.Anchor)));

        var html = manual.ToHtml(Path.GetFileName!);
        Assert.Equal(html, manual.ToHtml(Path.GetFileName!)); // подготовка не портит само руководство
        Assert.Contains("<h2 id=\"one\">1. Раз</h2>", html);
        Assert.Contains("<h3 id=\"sub\">1.1. Под</h3>", html);
        Assert.Contains("<figure><img src=\"x.png\" alt=\"Рисунок 1 — Окно &lt;1&gt;\">", html);
        Assert.Contains("<figcaption>Рисунок 2 — Ещё</figcaption>", html);
        Assert.DoesNotContain("<a id=", html); // якорь живёт в самом заголовке
        Assert.DoesNotContain("<h1", html);    // заголовок руководства печатает обложка
    }
}
