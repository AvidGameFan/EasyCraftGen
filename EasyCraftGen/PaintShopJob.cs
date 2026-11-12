using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyCraftGen;

public sealed class PaintShopJob
{
    private sealed record Request(
        [property: JsonPropertyName("version"), JsonRequired] int Version,
        [property: JsonPropertyName("width"), JsonRequired] int Width,
        [property: JsonPropertyName("height"), JsonRequired] int Height,
        [property: JsonPropertyName("selection"), JsonRequired] int[]? Selection);

    private readonly string directory;
    public DocumentInfo Document { get; }
    public string SourcePath => Path.Combine(directory, "composite.png");
    public string ResultPath => Path.Combine(directory, "result.png");
    private string ResponsePath => Path.Combine(directory, "response.json");

    private PaintShopJob(string directory, DocumentInfo document)
    {
        this.directory = directory;
        Document = document;
    }

    public static PaintShopJob Load(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "request.json")
            throw new ArgumentException("PaintShop Pro job must be an absolute path to request.json.");
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(path))
            ?? throw new InvalidDataException("PaintShop Pro job is empty.");
        if (request.Version != 1 || request.Width <= 0 || request.Height <= 0)
            throw new InvalidDataException("Invalid PaintShop Pro job version or dimensions.");
        var s = request.Selection;
        if (s is not null && (s.Length != 4 || s[0] < 0 || s[1] < 0 || s[2] <= 0 || s[3] <= 0 ||
                              (long)s[0] + s[2] > request.Width || (long)s[1] + s[3] > request.Height))
            throw new InvalidDataException("Invalid PaintShop Pro selection bounds.");
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Job directory is missing.");
        var result = new PaintShopJob(directory, new DocumentInfo("PaintShop Pro", "", request.Width, request.Height, s));
        if (!File.Exists(result.SourcePath)) throw new FileNotFoundException("PaintShop Pro composite not found.", result.SourcePath);
        if (File.Exists(result.ResultPath) || File.Exists(result.ResponsePath))
            throw new InvalidDataException("PaintShop Pro job has already been used.");
        return result;
    }

    public async Task CompleteAsync(byte[] png, CancellationToken ct)
    {
        if (ImageTools.Dimensions(png) != (Document.Width, Document.Height))
            throw new InvalidDataException("Generated image dimensions do not match the PaintShop Pro document.");
        await File.WriteAllBytesAsync(ResultPath, png, ct);
        ct.ThrowIfCancellationRequested();
        WriteResponse("success");
    }

    public void Cancel() => WriteResponse("cancelled");

    private void WriteResponse(string status)
    {
        string temporary = ResponsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, status }));
            File.Move(temporary, ResponsePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
