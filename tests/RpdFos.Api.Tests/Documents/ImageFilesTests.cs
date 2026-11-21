using System.Text;
using RpdFos.Core.Documents;
using Xunit;

namespace RpdFos.Api.Tests.Documents;

/// <summary>Проверка и очистка рисунка (этап 7, B4а): тип по сигнатуре, размеры, метаданные не остаются.</summary>
public class ImageFilesTests
{
    [Fact]
    public void Png_is_recognized_by_signature_and_text_chunks_are_dropped()
    {
        var source = TestImages.PngWithText("Иванов И. И., ноутбук 5");

        var (image, problem) = ImageFiles.Inspect(source);

        Assert.Null(problem);
        Assert.Equal((ImageFiles.Png, 40, 30), (image!.MimeType, image.Width, image.Height));
        Assert.True(image.Content.Length < source.Length);
        // Ни текстовой части, ни времени изменения: автор и модель компьютера в файл документа не попадают.
        var text = Encoding.Latin1.GetString(image.Content);
        Assert.DoesNotContain("tEXt", text);
        Assert.DoesNotContain("tIME", text);
        Assert.DoesNotContain("Author", text);
        Assert.Contains("IDAT", text);
        Assert.Contains("IEND", text);
        // Очищенный файл — тоже рисунок, а повторная очистка ничего не меняет.
        Assert.Equal(image.Content, ImageFiles.Inspect(image.Content).Image!.Content);
    }

    [Fact]
    public void Jpeg_keeps_frame_and_scan_and_drops_exif_and_comments()
    {
        var source = TestImages.Jpeg(64, 48);

        var (image, problem) = ImageFiles.Inspect(source);

        Assert.Null(problem);
        Assert.Equal((ImageFiles.Jpeg, ".jpg", 64, 48), (image!.MimeType, image.Extension, image.Width, image.Height));
        var text = Encoding.Latin1.GetString(image.Content);
        Assert.DoesNotContain("Exif", text);
        Assert.DoesNotContain("GPS", text);
        Assert.DoesNotContain("comment", text);
        // Данные скана и «FF 00» внутри него — на месте, файл по-прежнему оканчивается маркером конца.
        Assert.Equal([0xFF, 0xD8], image.Content[..2]);
        Assert.Equal([0xFF, 0xD9], image.Content[^2..]);
        Assert.Contains((byte)0x34, image.Content);
        Assert.Equal(image.Content, ImageFiles.Inspect(image.Content).Image!.Content);
    }

    [Fact]
    public void Progressive_jpeg_is_accepted()
    {
        Assert.Null(ImageFiles.Inspect(TestImages.Jpeg(progressive: true)).Problem);
    }

    [Theory]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000")]
    [InlineData("MZ\u0090\u0000\u0003\u0000")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("")]
    public void Other_content_is_not_an_image_whatever_the_name_says(string content)
    {
        var (image, problem) = ImageFiles.Inspect(Encoding.Latin1.GetBytes(content));

        Assert.Null(image);
        Assert.Equal(ImageFiles.NotImage, problem);
    }

    [Fact]
    public void Truncated_or_broken_files_are_damaged()
    {
        var png = TestImages.Png();
        var jpeg = TestImages.Jpeg();

        Assert.Equal(ImageFiles.Damaged, ImageFiles.Inspect(png[..(png.Length - 20)]).Problem);
        Assert.Equal(ImageFiles.Damaged, ImageFiles.Inspect(png[..20]).Problem);
        Assert.Equal(ImageFiles.Damaged, ImageFiles.Inspect(jpeg[..(jpeg.Length - 2)]).Problem);
        Assert.Equal(ImageFiles.Damaged, ImageFiles.Inspect(jpeg[..12]).Problem);
        // Сигнатура есть, а первая часть — не IHDR.
        var noHeader = png.ToArray();
        noHeader[12] = (byte)'X';
        Assert.Equal(ImageFiles.Damaged, ImageFiles.Inspect(noHeader).Problem);
    }

    [Fact]
    public void Side_beyond_the_limit_and_zero_side_are_refused()
    {
        Assert.Equal(ImageFiles.TooBigSides, ImageFiles.Inspect(TestImages.Png(ImageFiles.MaxSide + 1, 10)).Problem);
        Assert.Equal(ImageFiles.TooBigSides, ImageFiles.Inspect(TestImages.Jpeg(10, ImageFiles.MaxSide + 1)).Problem);
        Assert.Equal(ImageFiles.Damaged, ImageFiles.Inspect(TestImages.Png(0, 10)).Problem);
        Assert.Null(ImageFiles.Inspect(TestImages.Png(ImageFiles.MaxSide, ImageFiles.MaxSide)).Problem);
    }
}
