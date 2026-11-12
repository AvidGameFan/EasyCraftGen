using System.Text.Json;
using SkiaSharp;

namespace EasyCraftGen.Tests;

public class PaintShopJobTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "easycraftgen-test-" + Guid.NewGuid().ToString("N"));
    private string RequestPath => Path.Combine(folder, "request.json");

    public PaintShopJobTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "composite.png"), Image(128, 64));
    }

    private static byte[] Image(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return image.ToArray();
    }

    private void Request(string selection = "null", int width = 128, int height = 64, int version = 1) =>
        File.WriteAllText(RequestPath, $$"""{"version":{{version}},"width":{{width}},"height":{{height}},"selection":{{selection}}}""");

    [Theory]
    [InlineData("null")]
    [InlineData("[12,20,38,20]")]
    public async Task ValidJobWritesResultThenSuccess(string selection)
    {
        Request(selection);
        var job = PaintShopJob.Load(RequestPath);
        Assert.Equal(selection == "null", job.Document.Selection is null);
        await job.CompleteAsync(Image(128, 64), CancellationToken.None);
        Assert.Equal((128, 64), ImageTools.Dimensions(File.ReadAllBytes(job.ResultPath)));
        using var response = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "response.json")));
        Assert.Equal(1, response.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("success", response.RootElement.GetProperty("status").GetString());
        Assert.Throws<InvalidDataException>(() => PaintShopJob.Load(RequestPath));
    }

    [Fact]
    public void CancelWritesCancellationWithoutResult()
    {
        Request();
        var job = PaintShopJob.Load(RequestPath);
        job.Cancel();
        using var response = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "response.json")));
        Assert.Equal("cancelled", response.RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(job.ResultPath));
    }

    [Fact]
    public async Task InvalidResultAndCancellationDoNotPublishSuccess()
    {
        Request();
        var job = PaintShopJob.Load(RequestPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => job.CompleteAsync(Image(64, 64), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.CompleteAsync(Image(128, 64), cancelled.Token));
        Assert.False(File.Exists(Path.Combine(folder, "response.json")));
    }

    [Theory]
    [InlineData("[0,0,0,1]")]
    [InlineData("[90,0,50,5]")]
    [InlineData("[-1,0,4,4]")]
    [InlineData("[1,2,3]")]
    public void RejectsInvalidSelection(string selection)
    {
        Request(selection);
        Assert.Throws<InvalidDataException>(() => PaintShopJob.Load(RequestPath));
    }

    [Fact]
    public void RejectsWrongVersionAndMissingInput()
    {
        Request(version: 2);
        Assert.Throws<InvalidDataException>(() => PaintShopJob.Load(RequestPath));
        Request();
        File.Delete(Path.Combine(folder, "composite.png"));
        Assert.Throws<FileNotFoundException>(() => PaintShopJob.Load(RequestPath));
    }

    [Fact]
    public void RejectsMissingSelectionField()
    {
        File.WriteAllText(RequestPath, """{"version":1,"width":128,"height":64}""");
        Assert.Throws<JsonException>(() => PaintShopJob.Load(RequestPath));
    }

    [Fact]
    public void PaintShopModeDoesNotRequirePhotoCraftSettings()
    {
        var settings = new Settings { Port = 0, TokenPath = "", ExchangePath = "", UpscaleModel = "" };
        settings.Validate(photoCraft: false);
        Assert.Throws<ArgumentException>(() => settings.Validate());
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
