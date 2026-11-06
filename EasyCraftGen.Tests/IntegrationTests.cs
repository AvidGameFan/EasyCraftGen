using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace EasyCraftGen.Tests;

public class IntegrationTests
{
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
                return """{"stream":"/image/stream/123"}""";
            }
            return """{"step":1,"total_steps":2,"output":[{"path":"/image/tmp/123/0"}]}{"status":"succeeded","output":[{"data":"data:image/png;base64,AQI="}]}""";
        });
        using var client = new EasyDiffusionClient("http://localhost:9000", handler);
        var bytes = await client.FillAsync(new Settings { Model = "model" }, "prompt", "", [1, 2], [1, 2], 512, 512, new Progress<string>(), CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2 }, bytes);
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
        var data = ImageTools.PrepareFill(encoded.ToArray(), new DocumentInfo("test", "", 128, 128, [32, 16, 200, 48]));
        using var mask = SKBitmap.Decode(data.Mask);
        Assert.Equal((128, 128), (data.Width, data.Height));
        Assert.Equal(SKColors.Black, mask.GetPixel(10, 10));
        Assert.Equal(SKColors.White, mask.GetPixel(40, 20));
        Assert.Equal(SKColors.Black, mask.GetPixel(40, 80));
        Assert.Throws<InvalidOperationException>(() => ImageTools.PrepareFill(encoded.ToArray(), new DocumentInfo("test", "", 128, 128, null)));
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

    [Fact]
    public async Task PlacementPreservesSelectionAndMasksPastedLayer()
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
                    if (command == "file.close") temporary = false;
                    if (command == "document.inspect") result = new
                    {
                        name = temporary ? "result.png" : "original",
                        path = temporary ? "result.png" : "original.png",
                        width = 128, height = 64, selectionBounds = new[] { 4, 5, 10, 20 }
                    };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = root.GetProperty("id").GetInt32(), ok = true, result }));
            }
        });
        try
        {
            using (var client = await PhotoCraftClient.ConnectAsync(new Settings { Port = port, TokenPath = tokenPath }, CancellationToken.None))
                await client.PlaceAsync("result.png", new DocumentInfo("original", "original.png", 128, 64, [4, 5, 10, 20]), CancellationToken.None);
            await server;
            Assert.Equal(new[]
            {
                "select.saveSelection", "document.inspect", "select.all", "edit.copy", "file.close",
                "document.inspect", "edit.pasteSpecial.pasteInPlace", "select.loadSelection",
                "layer.layerMask.revealSelection", "layer.renameLayer", "document.inspect", "channel.delete"
            }, commands);
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
