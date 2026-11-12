using System.Text.Json;

namespace EasyCraftGen;

public sealed class Settings
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyCraftGen");
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
    public int FillMegapixels { get; set; } = 1;
    public int FillPixelStep { get; set; } = 64;
    public bool OptimizeSelection { get; set; }
    public int Scale { get; set; } = 2;
    public bool OnTop { get; set; } = true;

    public static Settings Load() => Load(DirectoryPath);

    public static Settings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Settings file is empty.")
            : new Settings();
        var generationPath = Path.Combine(directory, "generation-settings.json");
        if (File.Exists(generationPath))
        {
            var generation = JsonSerializer.Deserialize<GenerationSettings>(File.ReadAllText(generationPath))
                ?? throw new InvalidDataException("Generation settings file is empty.");
            generation.ApplyTo(settings);
        }
        return settings;
    }

    public void Save() => Save(DirectoryPath);

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        SaveGeneration(directory);
    }

    public void SaveGeneration() => SaveGeneration(DirectoryPath);

    public void SaveGeneration(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "generation-settings.json");
        var temp = Path.Combine(directory, $".generation-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(GenerationSettings.From(this), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Validate(bool photoCraft = true)
    {
        ValidateServerUrl(ServerUrl);
        if (photoCraft && (Port is < 1 or > 65535 || !Path.IsPathFullyQualified(TokenPath) || !Path.IsPathFullyQualified(ExchangePath)))
            throw new ArgumentException("Enter a valid port and absolute token and exchange paths.");
        if (string.IsNullOrWhiteSpace(Model) || (photoCraft && string.IsNullOrWhiteSpace(UpscaleModel)))
            throw new ArgumentException("Enter diffusion and upscaler model names.");
        if (string.IsNullOrWhiteSpace(Sampler) || string.IsNullOrWhiteSpace(Scheduler))
            throw new ArgumentException("Select a sampler and scheduler.");
        if (TextEncoders is null || TextEncoders.Any(string.IsNullOrWhiteSpace) || TextEncoders.Distinct(StringComparer.Ordinal).Count() != TextEncoders.Count)
            throw new ArgumentException("Text encoder names must be nonempty and unique.");
        if (Steps is < 1 or > 150 || Guidance is < 0 or > 30 || Scale is not (2 or 4))
            throw new ArgumentException("Invalid generation settings.");
        if (FillMegapixels is not (1 or 2 or 4 or 8))
            throw new ArgumentException("Select a fill resolution of 1, 2, 4, or 8 megapixels.");
        if (FillPixelStep is not (1 or 16 or 64))
            throw new ArgumentException("Select a fill pixel step of 1, 16, or 64 pixels.");
    }

    public static void ValidateServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter a valid HTTP(S) Easy Diffusion URL without credentials, query, or fragment.");
    }
}
