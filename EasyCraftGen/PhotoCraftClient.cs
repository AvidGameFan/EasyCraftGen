using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EasyCraftGen;

public sealed class PhotoCraftClient : IDisposable
{
    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private int id;

    private PhotoCraftClient(TcpClient client)
    {
        this.client = client;
        var stream = client.GetStream();
        reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public static async Task<PhotoCraftClient> ConnectAsync(Settings settings, CancellationToken ct)
    {
        var token = (await File.ReadAllTextAsync(settings.TokenPath, ct)).Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("PhotoCraft token file is empty.");
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync("127.0.0.1", settings.Port, ct);
            var result = new PhotoCraftClient(tcp);
            await result.CallAsync("auth", new { token }, ct);
            return result;
        }
        catch { tcp.Dispose(); throw; }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        var callerToken = ct;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        ct = timeout.Token;
        try
        {
            int requestId = ++id;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { id = requestId, method, @params = parameters ?? new { } }).AsMemory(), ct);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct) ?? throw new IOException("PhotoCraft closed the connection.");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || responseId.GetInt32() != requestId) continue;
                if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    throw new InvalidOperationException($"PhotoCraft {method}: {(root.TryGetProperty("error", out var error) ? error.ToString() : "request failed")}");
                return root.TryGetProperty("result", out var value) ? value.Clone() : default;
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException($"PhotoCraft did not respond to {method}. The command may still complete; check the document before retrying.");
        }
    }

    public Task<JsonElement> ExecuteAsync(string command, object? parameters, CancellationToken ct) =>
        CallAsync("engine.execute", new { command, @params = parameters ?? new { } }, ct);

    public async Task<DocumentInfo> InspectAsync(CancellationToken ct)
    {
        var result = await ExecuteAsync("document.inspect", null, ct);
        int[]? bounds = null;
        if (result.TryGetProperty("selectionBounds", out var selection) && selection.ValueKind == JsonValueKind.Array)
            bounds = selection.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        return new DocumentInfo(result.GetProperty("name").GetString() ?? "", result.TryGetProperty("path", out var path) ? path.ToString() : "",
            result.GetProperty("width").GetInt32(), result.GetProperty("height").GetInt32(), bounds);
    }

    public async Task<byte[]> ExportAsync(string exchange, CancellationToken ct)
    {
        Directory.CreateDirectory(exchange);
        var name = $"easycraftgen-export-{Guid.NewGuid():N}.png";
        var path = Path.Combine(exchange, name);
        try
        {
            await CallAsync("app.save", new { path = name }, ct);
            try
            {
                return await File.ReadAllBytesAsync(path, ct);
            }
            catch (FileNotFoundException ex)
            {
                throw new FileNotFoundException(
                    $"PhotoCraft reported a successful export, but the file was not found at {path}. " +
                    "Make sure EasyCraftGen's exchange directory matches PhotoCraft's automation write root, " +
                    "and its automation read root as well for placing results. Restart PhotoCraft after changing its roots.", path, ex);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    public async Task PlaceAsync(string name, DocumentInfo target, CancellationToken ct)
    {
        string channel = $"easycraftgen-{Guid.NewGuid():N}";
        await ExecuteAsync("select.saveSelection", new { name = channel }, ct);
        try
        {
            var opened = await CallAsync("app.open", new { path = name }, ct);
            var openedName = opened.GetProperty("name").GetString();
            var current = await InspectAsync(ct);
            if (string.IsNullOrEmpty(openedName) || current.Name != openedName || current.SameDocument(target))
                throw new InvalidOperationException("PhotoCraft did not open the generated image as expected; it was not pasted.");
            await ExecuteAsync("select.all", null, ct);
            await ExecuteAsync("edit.copy", null, ct);
            await ExecuteAsync("file.close", new { save = "discard" }, ct);
            current = await InspectAsync(ct);
            if (!current.SameDocument(target))
                throw new InvalidOperationException("PhotoCraft changed documents; the result was not pasted.");
            await ExecuteAsync("edit.pasteSpecial.pasteInPlace", null, ct);
            await ExecuteAsync("select.loadSelection", new { channel }, ct);
            await ExecuteAsync("layer.layerMask.revealSelection", null, ct);
            await ExecuteAsync("layer.renameLayer", new { name = "Generative Fill" }, ct);
        }
        finally
        {
            var current = await InspectAsync(CancellationToken.None);
            if (current.SameDocument(target))
                await ExecuteAsync("channel.delete", new { channel }, CancellationToken.None);
        }
    }

    public void Dispose() { writer.Dispose(); reader.Dispose(); client.Dispose(); }
}

public sealed record DocumentInfo(string Name, string Path, int Width, int Height, int[]? Selection)
{
    public bool SameDocument(DocumentInfo other) => Name == other.Name && Path == other.Path && Width == other.Width && Height == other.Height;
    public bool SameSelection(DocumentInfo other) => Selection is null ? other.Selection is null : other.Selection is not null && Selection.SequenceEqual(other.Selection);
}
