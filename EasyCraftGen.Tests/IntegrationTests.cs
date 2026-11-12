using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace EasyCraftGen.Tests;

public class IntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PaintShopJobRunsSharedGenerationAndReturnsFullSize(bool selected)
    {
        string folder = Path.Combine(Path.GetTempPath(), "easycraftgen-psp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var bitmap = new SKBitmap(800, 600);
            using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(folder, "composite.png"), encoded.ToArray());
            await File.WriteAllTextAsync(Path.Combine(folder, "request.json"),
                selected ? """{"version":1,"width":800,"height":600,"selection":[100,100,50,50]}"""
                         : """{"version":1,"width":800,"height":600,"selection":null}""");
            var job = PaintShopJob.Load(Path.Combine(folder, "request.json"));
            using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((request, body) =>
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                Assert.Equal(selected, root.TryGetProperty("mask", out _));
                Assert.Equal("Change the subject", root.GetProperty("prompt").GetString());
                var width = root.GetProperty("width").GetInt32();
                var height = root.GetProperty("height").GetInt32();
                Assert.True(width >= 384 && height >= 384);
                if (selected) Assert.True(width < 800);
                using var result = new SKBitmap(width, height);
                using (var canvas = new SKCanvas(result)) canvas.Clear(SKColors.Blue);
                using var png = result.Encode(SKEncodedImageFormat.Png, 100);
                return JsonSerializer.Serialize(new { status = "succeeded", output = new[] { new { data = Convert.ToBase64String(png.ToArray()) } } });
            }));
            var generated = await FillGeneration.GenerateAsync(client, new Settings { OptimizeSelection = true },
                "Change the subject", "", await File.ReadAllBytesAsync(job.SourcePath), job.Document,
                _ => { }, new Progress<string>(), CancellationToken.None);
            await job.CompleteAsync(generated, CancellationToken.None);
            using var restored = SKBitmap.Decode(job.ResultPath);
            Assert.Equal((800, 600), (restored.Width, restored.Height));
            Assert.Equal(SKColors.Blue, restored.GetPixel(110, 110));
            if (selected) Assert.Equal((byte)0, restored.GetPixel(799, 599).Alpha);
            else Assert.Equal(SKColors.Blue, restored.GetPixel(799, 599));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task FillUsesMaskAndHandlesConcatenatedStreamObjects()
    {
        var handler = new FakeHttp((request, body) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                Assert.Equal("/render", request.RequestUri!.AbsolutePath);
                Assert.Equal("data:image/png;base64,AQI=", root.GetProperty("mask").GetString());
                Assert.Equal("data:image/png;base64,AQI=", root.GetProperty("init_image").GetString());
                Assert.Equal("model", root.GetProperty("use_stable_diffusion_model").GetString());
                Assert.Equal("euler", root.GetProperty("sampler_name").GetString());
                Assert.Equal("simple", root.GetProperty("scheduler_name").GetString());
                Assert.Equal("", root.GetProperty("use_vae_model").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("use_text_encoder_model").ValueKind);
                return """{"stream":"/image/stream/123"}""";
            }
            return """{"step":1,"total_steps":2,"output":[{"path":"/image/tmp/123/0"}]}{"status":"succeeded","output":[{"data":"data:image/png;base64,AQI="}]}""";
        });
        using var client = new EasyDiffusionClient("http://localhost:9000", handler);
        var bytes = await client.FillAsync(new Settings { Model = "model" }, "prompt", "", [1, 2], [1, 2], 512, 512, new Progress<string>(), CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2 }, bytes);
    }

    [Fact]
    public async Task WholeImageEditSendsImageWithoutMask()
    {
        using var bitmap = new SKBitmap(128, 64);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), new DocumentInfo("test", "", 128, 64, null), 1);
        Assert.Null(prepared.Mask);
        using var source = SKBitmap.Decode(prepared.Source);
        Assert.Equal((384, 384), (source.Width, source.Height));
        Assert.Equal(SKColors.White, source.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, source.GetPixel(128, 160));
        Assert.Equal(SKColors.Red, source.GetPixel(255, 223));
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((request, body) =>
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("/render", request.RequestUri!.AbsolutePath);
            Assert.Equal("Remove the car", root.GetProperty("prompt").GetString());
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(prepared.Source), root.GetProperty("init_image").GetString());
            Assert.False(root.TryGetProperty("mask", out _));
            Assert.Equal(384, root.GetProperty("width").GetInt32());
            Assert.Equal(384, root.GetProperty("height").GetInt32());
            return """{"status":"succeeded","output":[{"data":"AQI="}]}""";
        }));
        Assert.Equal(new byte[] { 1, 2 }, await client.FillAsync(new Settings(), "Remove the car", "", prepared.Source,
            prepared.Mask, prepared.Width, prepared.Height, new Progress<string>(), CancellationToken.None));
    }

    [Fact]
    public void WholeImageEditUsesResolutionSettingsAndWhiteForTransparency()
    {
        using var bitmap = new SKBitmap(2048, 1024);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Transparent);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 2048, 1024, null);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 1);
        Assert.Null(prepared.Mask);
        Assert.Equal((1414, 707), (prepared.Width, prepared.Height));
        using var source = SKBitmap.Decode(prepared.Source);
        Assert.Equal((1414, 707), (source.Width, source.Height));
        Assert.Equal(SKColors.White, source.GetPixel(0, 0));
        Assert.False(doc.SameSelection(doc with { Selection = [1, 2, 3, 4] }));
        Assert.True(doc.SameSelection(doc with { }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FillSendsSamplerSchedulerVaeAndOrderedTextEncoders(int count)
    {
        var encoders = new[] { "clip_l", "t5xxl_fp16" }.Take(count).ToList();
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("dpmpp_2m", root.GetProperty("sampler_name").GetString());
            Assert.Equal("karras", root.GetProperty("scheduler_name").GetString());
            Assert.Equal("ae", root.GetProperty("use_vae_model").GetString());
            var encoded = root.GetProperty("use_text_encoder_model");
            if (count == 0) Assert.Equal(JsonValueKind.Null, encoded.ValueKind);
            else Assert.Equal(encoders, encoded.EnumerateArray().Select(e => e.GetString()).ToArray());
            return """{"status":"succeeded","output":[{"data":"AQI="}]}""";
        }));
        var settings = new Settings { Sampler = "dpmpp_2m", Scheduler = "karras", Vae = "ae", TextEncoders = encoders };
        await client.FillAsync(settings, "prompt", "", [1], [1], 512, 512, new Progress<string>(), CancellationToken.None);
    }

    [Fact]
    public async Task ModelCatalogUsesTagsAndPreservesModelIdentifiers()
    {
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((request, _) =>
        {
            Assert.Equal("/get/models", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            return """
                {"models":[
                  {"model":"folder/z-model","name":"A display label","tags":["stable-diffusion","sd_xl_base"]},
                  {"model":"a-model","name":"A model","tags":["stable-diffusion"]},
                  {"model":"ae","name":"AE","tags":["vae"]},
                  {"model":"clip_l","name":"CLIP","tags":["text-encoder"]},
                  {"model":"t5","name":"T5","tags":["text-encoder"]},
                  {"model":"RealESRGAN_x4plus","name":"upscaler","tags":["realesrgan"]},
                  {"model":"lora","name":"LoRA","tags":["lora"]},
                  {"model":"clip_l","name":"duplicate","tags":["text-encoder"]}]}
                """;
        }));
        var models = await client.GetModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "a-model", "folder/z-model" }, models.Diffusion);
        Assert.Equal(new[] { "ae" }, models.Vae);
        Assert.Equal(new[] { "clip_l", "t5" }, models.TextEncoders);
        Assert.Equal(new[] { "RealESRGAN_x4plus" }, models.Upscalers);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"models":{}}""")]
    [InlineData("""{"models":[{"model":"bad"}]}""")]
    [InlineData("""{"models":[{"model":"bad","tags":[12]}]}""")]
    public async Task ModelCatalogRejectsMalformedResponses(string response)
    {
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, _) => response));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetModelsAsync(CancellationToken.None));
    }

    [Fact]
    public void SettingsDeserializeOlderFilesAndRoundTripNewOptions()
    {
        var old = JsonSerializer.Deserialize<Settings>("""{"Model":"old"}""")!;
        Assert.Equal("euler", old.Sampler);
        Assert.Equal("simple", old.Scheduler);
        Assert.Equal(1, old.FillMegapixels);
        Assert.Equal(64, old.FillPixelStep);
        Assert.False(old.OptimizeSelection);
        Assert.Empty(old.TextEncoders);
        old.Sampler = "euler_a"; old.Scheduler = "karras"; old.Vae = "ae"; old.FillMegapixels = 8; old.FillPixelStep = 16;
        old.TextEncoders = ["clip_l", "t5xxl_fp16"]; old.OptimizeSelection = true;
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(old))!;
        Assert.Equal("euler_a", loaded.Sampler);
        Assert.Equal("karras", loaded.Scheduler);
        Assert.Equal("ae", loaded.Vae);
        Assert.Equal(8, loaded.FillMegapixels);
        Assert.Equal(16, loaded.FillPixelStep);
        Assert.True(loaded.OptimizeSelection);
        Assert.Equal(new[] { "clip_l", "t5xxl_fp16" }, loaded.TextEncoders);
    }

    [Fact]
    public async Task UpscaleUsesFilterEndpointAndRequestedScale()
    {
        var handler = new FakeHttp((request, body) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("/filter", request.RequestUri!.AbsolutePath);
            Assert.Equal("realesrgan", json.RootElement.GetProperty("filter").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("filter_params").GetProperty("realesrgan").GetProperty("scale").GetInt32());
            Assert.Equal("upscale-model", json.RootElement.GetProperty("model_paths").GetProperty("realesrgan").GetString());
            return """{"status":"succeeded","output":["AQI="]}""";
        });
        using var client = new EasyDiffusionClient("http://localhost:9000", handler);
        Assert.Equal(new byte[] { 1, 2 }, await client.UpscaleAsync(new Settings { UpscaleModel = "upscale-model" }, [1, 2], new Progress<string>(), CancellationToken.None));
    }

    [Fact]
    public async Task ServerFailureIsReported()
    {
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, _) => """{"status":"failed","detail":"out of memory"}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpscaleAsync(new Settings(), [1], new Progress<string>(), CancellationToken.None));
        Assert.Contains("out of memory", ex.Message);
    }

    [Fact]
    public async Task SuccessWithoutImagesFailsExplicitly()
    {
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, _) => """{"status":"succeeded"}"""));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => client.UpscaleAsync(new Settings(), [1], new Progress<string>(), CancellationToken.None));
        Assert.Contains("without an image", ex.Message);
    }

    [Fact]
    public void FillMaskUsesClippedSelectionAndOutputDimensions()
    {
        using var bitmap = new SKBitmap(128, 128);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var data = ImageTools.PrepareFill(encoded.ToArray(), new DocumentInfo("test", "", 128, 128, [32, 16, 200, 48]), 1);
        using var mask = SKBitmap.Decode(data.Mask);
        Assert.Equal((384, 384), (data.Width, data.Height));
        Assert.Equal(SKColors.Black, mask.GetPixel(10, 10));
        Assert.Equal(SKColors.White, mask.GetPixel(168, 148));
        Assert.Equal(SKColors.Black, mask.GetPixel(168, 208));
        Assert.Throws<InvalidOperationException>(() => ImageTools.PrepareFill(encoded.ToArray(), new DocumentInfo("test", "", 128, 128, [1, 2, 0, 4]), 1));
        Assert.Throws<InvalidOperationException>(() => ImageTools.PrepareFill(encoded.ToArray(), new DocumentInfo("test", "", 128, 128, [1, 2, 3]), 1));
    }

    [Theory]
    [InlineData(1, 1024)]
    [InlineData(2, 1408)]
    [InlineData(4, 1984)]
    [InlineData(8, 2816)]
    public async Task FillResolutionSetsImageMaskAndRequestDimensions(int megapixels, int expected)
    {
        using var bitmap = new SKBitmap(3072, 3072);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 3072, 3072, [768, 768, 1536, 1536]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, megapixels);
        Assert.Equal((expected, expected), (prepared.Width, prepared.Height));
        using var source = SKBitmap.Decode(prepared.Source);
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal((expected, expected), (source.Width, source.Height));
        Assert.Equal((expected, expected), (mask.Width, mask.Height));
        Assert.Equal(SKColors.Black, mask.GetPixel(0, 0));
        Assert.Equal(SKColors.White, mask.GetPixel(expected / 2, expected / 2));
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(expected, json.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(expected, json.RootElement.GetProperty("height").GetInt32());
            return """{"status":"succeeded","output":[{"data":"AQI="}]}""";
        }));
        await client.FillAsync(new Settings { FillMegapixels = megapixels }, "prompt", "", prepared.Source, prepared.Mask,
            prepared.Width, prepared.Height, new Progress<string>(), CancellationToken.None);
    }

    [Fact]
    public void FillResolutionDoesNotEnlargeSmallDocumentsAndRejectsUnsupportedValues()
    {
        using var bitmap = new SKBitmap(128, 64);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 128, 64, [0, 0, 64, 64]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 8);
        Assert.Equal((384, 384), (prepared.Width, prepared.Height));
        Assert.Equal(new SKRect(128, 160, 256, 224), prepared.Content);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTools.PrepareFill(encoded.ToArray(), doc, 3));
        Assert.Throws<ArgumentException>(() => new Settings { FillMegapixels = 3 }.Validate());
    }

    [Theory]
    [InlineData(1, 1408, 704)]
    [InlineData(2, 1984, 1024)]
    [InlineData(4, 2048, 1024)]
    public void FillResolutionPreservesLandscapeAspectRatioApproximately(int megapixels, int width, int height)
    {
        using var bitmap = new SKBitmap(2048, 1024);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 2048, 1024, [256, 128, 512, 512]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, megapixels);
        Assert.Equal((width, height), (prepared.Width, prepared.Height));
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal((width, height), (mask.Width, mask.Height));
        Assert.Equal(SKColors.White, mask.GetPixel(width / 4, height / 4));
        Assert.Equal(SKColors.Black, mask.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(64, 1408, 704)]
    [InlineData(16, 1408, 704)]
    [InlineData(1, 1414, 707)]
    public void FillPixelStepAlsoAppliesAfterMegapixelScaling(int step, int width, int height)
    {
        using var bitmap = new SKBitmap(2048, 1024);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 2048, 1024, [256, 128, 512, 512]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, step);
        Assert.Equal((width, height), (prepared.Width, prepared.Height));
    }

    [Theory]
    [InlineData(64, 1024, 704)]
    [InlineData(16, 1008, 672)]
    [InlineData(1, 1001, 677)]
    public async Task FillPixelStepControlsSourceMaskAndRequestDimensions(int step, int width, int height)
    {
        using var bitmap = new SKBitmap(1001, 677);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 1001, 677, [200, 100, 400, 300]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, step);
        Assert.Equal((width, height), (prepared.Width, prepared.Height));
        using var source = SKBitmap.Decode(prepared.Source);
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal((width, height), (source.Width, source.Height));
        Assert.Equal((width, height), (mask.Width, mask.Height));
        Assert.Equal(SKColors.White, mask.GetPixel(width * 3 / 10, height * 3 / 10));
        Assert.Equal(SKColors.Black, mask.GetPixel(0, 0));
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(width, json.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(height, json.RootElement.GetProperty("height").GetInt32());
            return """{"status":"succeeded","output":[{"data":"AQI="}]}""";
        }));
        await client.FillAsync(new Settings { FillPixelStep = step }, "prompt", "", prepared.Source, prepared.Mask,
            prepared.Width, prepared.Height, new Progress<string>(), CancellationToken.None);
    }

    [Fact]
    public void FillPixelStepHonorsMinimumAndRejectsUnsupportedValues()
    {
        using var bitmap = new SKBitmap(5, 7);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 5, 7, [0, 0, 5, 7]);
        var native = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 1);
        Assert.Equal((384, 384), (native.Width, native.Height));
        Assert.Equal(new SKRect(189, 188, 194, 195), native.Content);
        var small = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 16);
        Assert.Equal((384, 384), (small.Width, small.Height));
        Assert.Equal(new SKRect(184, 184, 200, 200), small.Content);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 8));
        Assert.Throws<ArgumentException>(() => new Settings { FillPixelStep = 8 }.Validate());
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(980, 0, 616, 0)]
    [InlineData(0, 980, 0, 616)]
    [InlineData(980, 980, 616, 616)]
    [InlineData(500, 600, 318, 418)]
    public void OptimizedSelectionExpandsSmallCropAndClipsAtCanvasEdges(int x, int y, int left, int top)
    {
        using var bitmap = new SKBitmap(1000, 1000);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);
        using var paint = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(x, y, 20, 20, paint);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 1000, 1000, [x, y, 20, 20]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 1, true);
        Assert.Equal(new SKRectI(left, top, left + 384, top + 384), prepared.Crop);
        Assert.Equal((384, 384), (prepared.Width, prepared.Height));
        using var source = SKBitmap.Decode(prepared.Source);
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal(SKColors.Blue, source.GetPixel(x - left + 10, y - top + 10));
        Assert.Equal(SKColors.White, mask.GetPixel(x - left + 10, y - top + 10));
        Assert.Equal(SKColors.Black, mask.GetPixel(x == left ? 100 : 0, y == top ? 100 : 0));
        using var restored = SKBitmap.Decode(ImageTools.RestoreFill(prepared.Source, doc, prepared));
        Assert.Equal((1000, 1000), (restored.Width, restored.Height));
        Assert.Equal(SKColors.Blue, restored.GetPixel(x + 10, y + 10));
        int outsideX = left == 0 ? 999 : 0;
        int outsideY = top == 0 ? 999 : 0;
        Assert.Equal(0, restored.GetPixel(outsideX, outsideY).Alpha);
    }

    [Fact]
    public async Task OptimizedSelectionSendsBorderAndRestoresScaledResultAtOffset()
    {
        using var bitmap = new SKBitmap(3000, 2000);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 3000, 2000, [1000, 700, 1200, 800]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 16, true);
        Assert.Equal(new SKRectI(936, 636, 2264, 1564), prepared.Crop);
        Assert.Equal((1200, 832), (prepared.Width, prepared.Height));
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal(SKColors.Black, mask.GetPixel(10, 10));
        Assert.Equal(SKColors.White, mask.GetPixel(prepared.Width / 2, prepared.Height / 2));
        using var mappedMask = SKBitmap.Decode(ImageTools.RestoreFill(prepared.Mask!, doc, prepared));
        Assert.Equal(SKColors.Black, mappedMask.GetPixel(990, 690));
        Assert.Equal(SKColors.White, mappedMask.GetPixel(1010, 710));
        Assert.Equal(SKColors.White, mappedMask.GetPixel(2190, 1490));
        Assert.Equal(SKColors.Black, mappedMask.GetPixel(2210, 1510));
        using var client = new EasyDiffusionClient("http://localhost:9000", new FakeHttp((_, body) =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(1200, json.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(832, json.RootElement.GetProperty("height").GetInt32());
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(prepared.Mask!), json.RootElement.GetProperty("mask").GetString());
            return """{"status":"succeeded","output":[{"data":"AQI="}]}""";
        }));
        await client.FillAsync(new Settings(), "fill", "", prepared.Source, prepared.Mask, prepared.Width, prepared.Height,
            new Progress<string>(), CancellationToken.None);
        using var generated = new SKBitmap(600, 416);
        using (var canvas = new SKCanvas(generated)) canvas.Clear(SKColors.Blue);
        using var generatedPng = generated.Encode(SKEncodedImageFormat.Png, 100);
        using var restored = SKBitmap.Decode(ImageTools.RestoreFill(generatedPng.ToArray(), doc, prepared));
        Assert.Equal(0, restored.GetPixel(935, 700).Alpha);
        Assert.Equal(SKColors.Blue, restored.GetPixel(936, 636));
        Assert.Equal(SKColors.Blue, restored.GetPixel(2263, 1563));
        Assert.Equal(0, restored.GetPixel(2264, 1563).Alpha);
    }

    [Fact]
    public void OptimizeIsIgnoredWithoutSelectionAndMinimumPaddingIsRemovedOnRestore()
    {
        using var bitmap = new SKBitmap(128, 64);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 128, 64, null);
        var ordinary = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 1);
        var optimized = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 1, true);
        Assert.Equal(ordinary.Crop, optimized.Crop);
        Assert.Equal(ordinary.Content, optimized.Content);
        Assert.Equal(ordinary.Source, optimized.Source);
        Assert.Null(optimized.Mask);
        using var restored = SKBitmap.Decode(ImageTools.RestoreFill(optimized.Source, doc, optimized));
        Assert.Equal((128, 64), (restored.Width, restored.Height));
        Assert.Equal(SKColors.Red, restored.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, restored.GetPixel(127, 63));
    }

    [Fact]
    public void OptimizedSelectionOnSmallCanvasKeepsMaskAndPlacementAligned()
    {
        using var bitmap = new SKBitmap(128, 64);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 128, 64, [30, 20, 20, 10]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 1, true);
        Assert.Equal(new SKRectI(0, 0, 128, 64), prepared.Crop);
        Assert.Equal((384, 384), (prepared.Width, prepared.Height));
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal(SKColors.White, mask.GetPixel(163, 185));
        Assert.Equal(SKColors.Black, mask.GetPixel(10, 10));
        using var restored = SKBitmap.Decode(ImageTools.RestoreFill(prepared.Mask!, doc, prepared));
        Assert.Equal(SKColors.White, restored.GetPixel(35, 25));
        Assert.Equal(SKColors.Black, restored.GetPixel(10, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VeryWideImageIsPaddedAfterResolutionScaling(bool optimize)
    {
        using var bitmap = new SKBitmap(8000, 400);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var doc = new DocumentInfo("test", "", 8000, 400, [0, 0, 8000, 400]);
        var prepared = ImageTools.PrepareFill(encoded.ToArray(), doc, 1, 16, optimize);
        Assert.Equal((4480, 384), (prepared.Width, prepared.Height));
        Assert.Equal(new SKRect(0, 80, 4480, 304), prepared.Content);
        using var mask = SKBitmap.Decode(prepared.Mask);
        Assert.Equal(SKColors.Black, mask.GetPixel(10, 0));
        Assert.Equal(SKColors.White, mask.GetPixel(10, 100));
        Assert.Equal(SKColors.Black, mask.GetPixel(10, 383));
    }

    [Fact]
    public async Task PhotoCraftAuthenticatesAndInspectsDocument()
    {
        var tokenPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenPath, "secret");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var auth = JsonDocument.Parse((await reader.ReadLineAsync())!);
            Assert.Equal("auth", auth.RootElement.GetProperty("method").GetString());
            Assert.Equal("secret", auth.RootElement.GetProperty("params").GetProperty("token").GetString());
            await writer.WriteLineAsync("""{"id":1,"ok":true,"result":{}}""");
            using var inspect = JsonDocument.Parse((await reader.ReadLineAsync())!);
            Assert.Equal("document.inspect", inspect.RootElement.GetProperty("params").GetProperty("command").GetString());
            await writer.WriteLineAsync("""{"id":2,"ok":true,"result":{"name":"image","path":null,"width":128,"height":64,"selectionBounds":[4,5,10,20]}}""");
        });
        try
        {
            using var client = await PhotoCraftClient.ConnectAsync(new Settings { Port = port, TokenPath = tokenPath }, CancellationToken.None);
            var doc = await client.InspectAsync(CancellationToken.None);
            Assert.Equal("image", doc.Name);
            Assert.Equal(new[] { 4, 5, 10, 20 }, doc.Selection);
            await server;
        }
        finally { listener.Stop(); File.Delete(tokenPath); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportReadsConfiguredRootOrExplainsMismatch(bool matchingRoots)
    {
        var root = Path.Combine(Path.GetTempPath(), $"easycraftgen-test-{Guid.NewGuid():N}");
        var configured = Path.Combine(root, "configured");
        var actual = matchingRoots ? configured : Path.Combine(root, "actual");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(actual);
        var tokenPath = Path.Combine(root, "token");
        await File.WriteAllTextAsync(tokenPath, "secret");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? exported = null;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var auth = JsonDocument.Parse((await reader.ReadLineAsync())!);
            await writer.WriteLineAsync("""{"id":1,"ok":true,"result":{}}""");
            using var save = JsonDocument.Parse((await reader.ReadLineAsync())!);
            Assert.Equal("app.save", save.RootElement.GetProperty("method").GetString());
            exported = Path.Combine(actual, save.RootElement.GetProperty("params").GetProperty("path").GetString()!);
            await File.WriteAllBytesAsync(exported, [1, 2, 3]);
            await writer.WriteLineAsync("""{"id":2,"ok":true,"result":{}}""");
        });
        try
        {
            using var client = await PhotoCraftClient.ConnectAsync(new Settings { Port = port, TokenPath = tokenPath }, CancellationToken.None);
            if (matchingRoots)
                Assert.Equal(new byte[] { 1, 2, 3 }, await client.ExportAsync(configured, CancellationToken.None));
            else
            {
                var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => client.ExportAsync(configured, CancellationToken.None));
                Assert.Contains(configured, ex.Message);
                Assert.Contains("automation write root", ex.Message);
                Assert.True(File.Exists(exported));
            }
            await server;
            if (matchingRoots) Assert.False(File.Exists(exported));
        }
        finally
        {
            listener.Stop();
            if (exported is not null && File.Exists(exported)) File.Delete(exported);
            File.Delete(tokenPath);
            if (!matchingRoots) Directory.Delete(actual);
            Directory.Delete(configured);
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlacementMasksOnlyWhenSelectionExists(bool hasSelection)
    {
        var tokenPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(tokenPath, "secret");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            bool temporary = false;
            string? channel = null;
            while (await reader.ReadLineAsync() is { } line)
            {
                using var request = JsonDocument.Parse(line);
                var root = request.RootElement;
                var method = root.GetProperty("method").GetString()!;
                var parameters = root.GetProperty("params");
                object result = new { };
                if (method == "app.open") { temporary = true; result = new { name = "result.png" }; }
                if (method == "engine.execute")
                {
                    var command = parameters.GetProperty("command").GetString()!;
                    commands.Add(command);
                    var args = parameters.GetProperty("params");
                    if (command == "select.saveSelection") channel = args.GetProperty("name").GetString();
                    if (command is "select.loadSelection" or "channel.delete") Assert.Equal(channel, args.GetProperty("channel").GetString());
                    if (command == "layer.renameLayer") Assert.Equal(hasSelection ? "Generative Fill" : "Generative Edit", args.GetProperty("name").GetString());
                    if (command == "file.close") temporary = false;
                    if (command == "document.inspect") result = new
                    {
                        name = temporary ? "result.png" : "original",
                        path = temporary ? "result.png" : "original.png",
                        width = 128, height = 64, selectionBounds = hasSelection ? new[] { 4, 5, 10, 20 } : null
                    };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = root.GetProperty("id").GetInt32(), ok = true, result }));
            }
        });
        try
        {
            using (var client = await PhotoCraftClient.ConnectAsync(new Settings { Port = port, TokenPath = tokenPath }, CancellationToken.None))
                await client.PlaceAsync("result.png", new DocumentInfo("original", "original.png", 128, 64, hasSelection ? [4, 5, 10, 20] : null), CancellationToken.None);
            await server;
            var expected = hasSelection ? new[]
            {
                "select.saveSelection", "document.inspect", "select.all", "edit.copy", "file.close",
                "document.inspect", "edit.pasteSpecial.pasteInPlace", "select.loadSelection",
                "layer.layerMask.revealSelection", "layer.renameLayer", "document.inspect", "channel.delete"
            } : new[]
            {
                "document.inspect", "select.all", "edit.copy", "file.close",
                "document.inspect", "edit.pasteSpecial.pasteInPlace", "layer.renameLayer"
            };
            Assert.Equal(expected, commands);
        }
        finally { listener.Stop(); File.Delete(tokenPath); }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, string, string> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request, body), Encoding.UTF8, "application/json") };
        }
    }
}
