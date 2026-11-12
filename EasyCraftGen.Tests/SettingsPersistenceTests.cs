using System.Text.Json;

namespace EasyCraftGen.Tests;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "easycraftgen-settings-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GenerationSavePersistsChoicesWithoutChangingConnectionSettings()
    {
        var saved = new Settings { ServerUrl = "http://localhost:9000", Port = 7890, OnTop = true };
        saved.Save(directory);
        var connectionFile = Path.Combine(directory, "settings.json");
        var before = File.ReadAllBytes(connectionFile);
        var current = Settings.Load(directory);
        current.ServerUrl = "http://localhost:9999"; current.Port = 9999; current.OnTop = false;
        current.Model = "new-model"; current.Sampler = "heun"; current.Scheduler = "karras";
        current.Vae = "vae"; current.TextEncoders = ["clip", "t5"]; current.UpscaleModel = "upscaler";
        current.Steps = 42; current.Guidance = 4.5; current.FillMegapixels = 4;
        current.FillPixelStep = 16; current.OptimizeSelection = true; current.Scale = 4;
        current.SaveGeneration(directory);

        Assert.Equal(before, File.ReadAllBytes(connectionFile));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "generation-settings.json")));
        Assert.False(json.RootElement.TryGetProperty("ServerUrl", out _));
        Assert.False(json.RootElement.TryGetProperty("TokenPath", out _));
        var loaded = Settings.Load(directory);
        Assert.Equal(saved.ServerUrl, loaded.ServerUrl);
        Assert.Equal(saved.Port, loaded.Port);
        Assert.True(loaded.OnTop);
        Assert.Equal("new-model", loaded.Model);
        Assert.Equal("heun", loaded.Sampler);
        Assert.Equal("karras", loaded.Scheduler);
        Assert.Equal("vae", loaded.Vae);
        Assert.Equal(new[] { "clip", "t5" }, loaded.TextEncoders);
        Assert.Equal("upscaler", loaded.UpscaleModel);
        Assert.Equal(42, loaded.Steps);
        Assert.Equal(4.5, loaded.Guidance);
        Assert.Equal(4, loaded.FillMegapixels);
        Assert.Equal(16, loaded.FillPixelStep);
        Assert.True(loaded.OptimizeSelection);
        Assert.Equal(4, loaded.Scale);
        current.Model = "second-model";
        current.SaveGeneration(directory);
        Assert.Equal("second-model", Settings.Load(directory).Model);
    }

    [Fact]
    public void ExistingSettingsRemainDefaultsUntilGenerationSettingsAreSaved()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            """{"ServerUrl":"http://localhost:9001","Model":"legacy","Sampler":"heun","FillPixelStep":16}""");
        var loaded = Settings.Load(directory);
        Assert.Equal("legacy", loaded.Model);
        Assert.Equal("heun", loaded.Sampler);
        Assert.Equal(16, loaded.FillPixelStep);
        loaded.SaveGeneration(directory);
        var migrated = Settings.Load(directory);
        Assert.Equal("legacy", migrated.Model);
        Assert.Equal("http://localhost:9001", migrated.ServerUrl);
    }

    [Fact]
    public void ManualSaveUpdatesBothFiles()
    {
        var settings = new Settings { Model = "first" };
        settings.Save(directory);
        settings.Model = "second";
        settings.ServerUrl = "http://localhost:9999";
        settings.Save(directory);
        var loaded = Settings.Load(directory);
        Assert.Equal("second", loaded.Model);
        Assert.Equal(settings.ServerUrl, loaded.ServerUrl);
    }

    [Fact]
    public void MalformedGenerationSettingsAreReported()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "generation-settings.json"), """{"Model":"incomplete"}""");
        Assert.Throws<JsonException>(() => Settings.Load(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
