using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Security.Cryptography;

namespace EasyCraftGen;

public partial class MainWindow : Window
{
    private CancellationTokenSource? job;

    public MainWindow()
    {
        InitializeComponent();
        try { ApplySettings(Settings.Load()); }
        catch (Exception ex) { Status.Text = $"Could not load settings: {ex.Message}"; ApplySettings(new Settings()); }
    }

    private void ApplySettings(Settings s)
    {
        ServerUrl.Text = s.ServerUrl; Port.Value = s.Port; TokenPath.Text = s.TokenPath; ExchangePath.Text = s.ExchangePath;
        Model.Text = s.Model; UpscaleModel.Text = s.UpscaleModel; Steps.Value = s.Steps; Guidance.Value = (decimal)s.Guidance;
        Scale.SelectedIndex = s.Scale == 4 ? 1 : 0; OnTop.IsChecked = s.OnTop; Topmost = s.OnTop;
    }

    private Settings ReadSettings()
    {
        var s = new Settings
        {
            ServerUrl = ServerUrl.Text?.Trim() ?? "", Port = (int)(Port.Value ?? 0),
            TokenPath = TokenPath.Text?.Trim() ?? "", ExchangePath = ExchangePath.Text?.Trim() ?? "",
            Model = Model.Text?.Trim() ?? "", UpscaleModel = UpscaleModel.Text?.Trim() ?? "",
            Steps = (int)(Steps.Value ?? 0), Guidance = (double)(Guidance.Value ?? 0),
            Scale = Scale.SelectedIndex == 1 ? 4 : 2, OnTop = OnTop.IsChecked == true
        };
        s.Validate();
        return s;
    }

    private void OnTopChanged(object? sender, RoutedEventArgs e) => Topmost = (sender as CheckBox)?.IsChecked == true;
    private void CancelClick(object? sender, RoutedEventArgs e) => job?.Cancel();
    private void SaveClick(object? sender, RoutedEventArgs e)
    {
        try { ReadSettings().Save(); Status.Text = "Settings saved."; }
        catch (Exception ex) { Status.Text = $"Could not save settings: {ex.Message}"; }
    }

    private async void TestClick(object? sender, RoutedEventArgs e) => await RunAsync(async (s, ct) =>
    {
        using var pc = await PhotoCraftClient.ConnectAsync(s, ct);
        await pc.CallAsync("ui.inspect", null, ct);
        using var easy = new EasyDiffusionClient(s.ServerUrl);
        await easy.TestAsync(ct);
        Status.Text = "PhotoCraft and Easy Diffusion connected. Exchange directory access was not tested.";
    });

    private async void FillClick(object? sender, RoutedEventArgs e) => await RunAsync(async (s, ct) =>
    {
        var prompt = Prompt.Text?.Trim() ?? "";
        var negative = NegativePrompt.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(prompt)) throw new ArgumentException("Enter a prompt for the fill.");
        Status.Text = "Reading PhotoCraft document...";
        using var pc = await PhotoCraftClient.ConnectAsync(s, ct);
        var doc = await pc.InspectAsync(ct);
        var original = await pc.ExportAsync(s.ExchangePath, ct);
        var prepared = ImageTools.PrepareFill(original, doc);
        using var easy = new EasyDiffusionClient(s.ServerUrl);
        Status.Text = "Generating fill in Easy Diffusion...";
        var generated = await easy.FillAsync(s, prompt, negative, prepared.Source, prepared.Mask, prepared.Width, prepared.Height,
            new Progress<string>(message => Status.Text = message), ct);
        generated = ImageTools.Resize(generated, doc.Width, doc.Height);
        ct.ThrowIfCancellationRequested();
        var current = await pc.InspectAsync(ct);
        if (!doc.SameDocument(current) || !doc.SameSelection(current))
            throw new InvalidOperationException("The document or selection changed during generation; no result was pasted.");
        var currentImage = await pc.ExportAsync(s.ExchangePath, ct);
        if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(currentImage)))
            throw new InvalidOperationException("The document changed during generation; no result was pasted.");
        var name = $"easycraftgen-fill-{Guid.NewGuid():N}.png";
        var path = Path.Combine(s.ExchangePath, name);
        await File.WriteAllBytesAsync(path, generated, ct);
        Status.Text = "Placing masked layer in PhotoCraft...";
        CancelButton.IsEnabled = false;
        try { await pc.PlaceAsync(name, doc, CancellationToken.None); }
        catch (Exception ex) { throw new InvalidOperationException($"Placement failed. Result saved at {path}. PhotoCraft may contain partial changes: {ex.Message}", ex); }
        File.Delete(path);
        Status.Text = "Fill added as a masked layer.";
    });

    private async void UpscaleClick(object? sender, RoutedEventArgs e) => await RunAsync(async (s, ct) =>
    {
        Status.Text = "Reading PhotoCraft document...";
        using var pc = await PhotoCraftClient.ConnectAsync(s, ct);
        var doc = await pc.InspectAsync(ct);
        if ((long)doc.Width * s.Scale > 8192 || (long)doc.Height * s.Scale > 8192)
            throw new InvalidOperationException("Upscaled dimensions exceed the 8192-pixel per side limit.");
        var original = await pc.ExportAsync(s.ExchangePath, ct);
        using var easy = new EasyDiffusionClient(s.ServerUrl);
        Status.Text = "Upscaling in Easy Diffusion...";
        var result = await easy.UpscaleAsync(s, original, new Progress<string>(message => Status.Text = message), ct);
        if (ImageTools.Dimensions(result) != (doc.Width * s.Scale, doc.Height * s.Scale))
            throw new InvalidDataException("Upscaler returned unexpected dimensions; no document was opened.");
        var name = $"easycraftgen-upscale-{Guid.NewGuid():N}.png";
        var path = Path.Combine(s.ExchangePath, name);
        await File.WriteAllBytesAsync(path, result, ct);
        CancelButton.IsEnabled = false;
        try { await pc.CallAsync("app.open", new { path = name }, CancellationToken.None); }
        catch (Exception ex) { throw new InvalidOperationException($"Opening failed. Result saved at {path}: {ex.Message}", ex); }
        Status.Text = $"Opened {s.Scale}x result in PhotoCraft. File: {path}";
    });

    private async Task RunAsync(Func<Settings, CancellationToken, Task> action)
    {
        if (job is not null) return;
        job = new CancellationTokenSource();
        FillButton.IsEnabled = UpscaleButton.IsEnabled = false; CancelButton.IsEnabled = true;
        try { await action(ReadSettings(), job.Token); }
        catch (OperationCanceledException) when (job.IsCancellationRequested) { Status.Text = "Cancelled locally. Easy Diffusion may still finish its queued job."; }
        catch (OperationCanceledException) { Status.Text = "The request timed out. Easy Diffusion may still finish its queued job."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { job.Dispose(); job = null; FillButton.IsEnabled = UpscaleButton.IsEnabled = true; CancelButton.IsEnabled = false; }
    }
}
