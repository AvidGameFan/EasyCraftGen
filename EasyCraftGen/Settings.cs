using System.Text.Json;

namespace EasyCraftGen;

public sealed class Settings
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyCraftGen");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    public string ServerUrl { get; set; } = "http://127.0.0.1:9000";
    public int Port { get; set; } = 7878;
    public string TokenPath { get; set; } = Path.Combine(DirectoryPath, "control.token");
    public string ExchangePath { get; set; } = Path.Combine(DirectoryPath, "exchange");
    public string Model { get; set; } = "sd-v1-5";
    public string Sampler { get; set; } = "euler";
    public string Scheduler { get; set; } = "simple";
    public string Vae { get; set; } = "";
    public List<string> TextEncoders { get; set; } = [];
    public string UpscaleModel { get; set; } = "RealESRGAN_x4plus";
    public int Steps { get; set; } = 25;
    public double Guidance { get; set; } = 7.5;
    public int Scale { get; set; } = 2;
    public bool OnTop { get; set; } = true;

    public static Settings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Settings file is empty.")
        : new Settings();

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Validate()
    {
        ValidateServerUrl(ServerUrl);
        if (Port is < 1 or > 65535 || !Path.IsPathFullyQualified(TokenPath) || !Path.IsPathFullyQualified(ExchangePath))
            throw new ArgumentException("Enter a valid port and absolute token and exchange paths.");
        if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(UpscaleModel))
            throw new ArgumentException("Enter diffusion and upscaler model names.");
        if (string.IsNullOrWhiteSpace(Sampler) || string.IsNullOrWhiteSpace(Scheduler))
            throw new ArgumentException("Select a sampler and scheduler.");
        if (TextEncoders is null || TextEncoders.Any(string.IsNullOrWhiteSpace) || TextEncoders.Distinct(StringComparer.Ordinal).Count() != TextEncoders.Count)
            throw new ArgumentException("Text encoder names must be nonempty and unique.");
        if (Steps is < 1 or > 150 || Guidance is < 0 or > 30 || Scale is not (2 or 4))
            throw new ArgumentException("Invalid generation settings.");
    }

    public static void ValidateServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter a valid HTTP(S) Easy Diffusion URL without credentials, query, or fragment.");
    }
}
