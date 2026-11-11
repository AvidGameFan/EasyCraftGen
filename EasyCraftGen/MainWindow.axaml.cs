using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Security.Cryptography;

namespace EasyCraftGen;

public partial class MainWindow : Window
{
    private CancellationTokenSource? job;
    private static readonly string[] SamplerOptions =
    [
        "euler", "euler_a", "ddim", "ddim_cfgpp", "plms", "heun", "heun_pp2", "dpm2", "dpm2_a",
        "dpm_fast", "dpm_adaptive", "lms", "dpm_solver_stability", "dpmpp_2s_a", "dpmpp_2m",
        "dpmpp_2m_v2", "dpmpp_2m_sde", "dpmpp_2m_sde_heun", "dpmpp_3m_sde", "dpmpp_sde",
        "restart", "ipndm", "ipndm_v", "ddpm", "deis", "lcm", "forge_flux_realistic",
        "forge_flux_realistic_slow", "unipc_snr", "unipc_tu", "unipc_snr_2", "unipc_tu_2", "unipc_tq", "tcd"
    ];
    private static readonly string[] SchedulerOptions =
    [
        "simple", "automatic", "uniform", "karras", "exponential", "polyexponential", "sgm_uniform", "kl_optimal",
        "normal", "ddim", "beta", "turbo", "align_your_steps", "align_your_steps_GITS", "align_your_steps_11", "align_your_steps_32"
    ];

    public MainWindow()
    {
        InitializeComponent();
        PromptStarters.ItemsSource = new[] { "Enhance ", "Colorize ", "Harmonize ", "Create ", "Change the style to ", "Remove " };
        try { ApplySettings(Settings.Load()); }
        catch (Exception ex) { Status.Text = $"Could not load settings: {ex.Message}"; ApplySettings(new Settings()); }
        ServerUrl.TextChanged += (_, _) => ClearModelChoices();
        Opened += async (_, _) => await RefreshModelsAsync();
    }

    private void ClearModelChoices()
    {
        UpdateModelChoices(Model, null);
        UpdateModelChoices(Vae, null);
        EncoderChoices.ItemsSource = null;
        UpdateModelChoices(UpscaleModel, null);
    }

    private void ApplySettings(Settings s)
    {
        ServerUrl.Text = s.ServerUrl; Port.Value = s.Port; TokenPath.Text = s.TokenPath; ExchangePath.Text = s.ExchangePath;
        Model.Text = s.Model; UpscaleModel.Text = s.UpscaleModel; Steps.Value = s.Steps; Guidance.Value = (decimal)s.Guidance;
        Vae.Text = s.Vae; TextEncoders.Text = string.Join(Environment.NewLine, s.TextEncoders);
        FillMegapixels.ItemsSource = new[] { 1, 2, 4, 8 };
        FillMegapixels.SelectedItem = s.FillMegapixels;
        FillPixelStep.ItemsSource = new[] { 64, 16, 1 };
        FillPixelStep.SelectedItem = s.FillPixelStep;
        OptimizeSelection.IsChecked = s.OptimizeSelection;
        SetOptions(Sampler, SamplerOptions, s.Sampler);
        SetOptions(Scheduler, SchedulerOptions, s.Scheduler);
        Scale.SelectedIndex = s.Scale == 4 ? 1 : 0; OnTop.IsChecked = s.OnTop; Topmost = s.OnTop;
    }

    private static void SetOptions(ComboBox combo, string[] options, string selected)
    {
        combo.ItemsSource = options.Contains(selected, StringComparer.Ordinal) ? options : [.. options, selected];
        combo.SelectedItem = selected;
    }

    private Settings ReadSettings()
    {
        var s = new Settings
        {
            ServerUrl = ServerUrl.Text?.Trim() ?? "", Port = (int)(Port.Value ?? 0),
            TokenPath = TokenPath.Text?.Trim() ?? "", ExchangePath = ExchangePath.Text?.Trim() ?? "",
            Model = Model.Text?.Trim() ?? "", UpscaleModel = UpscaleModel.Text?.Trim() ?? "",
            Sampler = Sampler.SelectedItem as string ?? "", Scheduler = Scheduler.SelectedItem as string ?? "",
            Vae = Vae.Text?.Trim() ?? "",
            TextEncoders = (TextEncoders.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Steps = (int)(Steps.Value ?? 0), Guidance = (double)(Guidance.Value ?? 0),
            FillMegapixels = FillMegapixels.SelectedItem is int megapixels ? megapixels : 0,
            FillPixelStep = FillPixelStep.SelectedItem is int pixelStep ? pixelStep : 0,
            OptimizeSelection = OptimizeSelection.IsChecked == true,
            Scale = Scale.SelectedIndex == 1 ? 4 : 2, OnTop = OnTop.IsChecked == true
        };
        s.Validate();
        return s;
    }

    private void OnTopChanged(object? sender, RoutedEventArgs e) => Topmost = (sender as CheckBox)?.IsChecked == true;
    private void CancelClick(object? sender, RoutedEventArgs e) => job?.Cancel();
    private static void UpdateModelChoices(ComboBox choice, string[]? values)
    {
        var text = choice.Text;
        choice.ItemsSource = values;
        choice.Text = text;
    }

    private void EncoderChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (EncoderChoices.SelectedItem is not string value) return;
        var existing = (TextEncoders.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!existing.Contains(value, StringComparer.Ordinal))
            TextEncoders.Text = string.Join(Environment.NewLine, existing.Append(value));
        EncoderChoices.SelectedIndex = -1;
    }

    private void PromptStarterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PromptStarters.SelectedItem is not string starter) return;
        var text = Prompt.Text ?? "";
        Prompt.Text = text + (text.Length > 0 && !char.IsWhiteSpace(text[^1]) ? " " : "") + starter;
        PromptStarters.SelectedIndex = -1;
        Prompt.Focus();
        Prompt.CaretIndex = Prompt.Text.Length;
    }

    private async void RefreshModelsClick(object? sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        if (job is not null) return;
        job = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FillButton.IsEnabled = UpscaleButton.IsEnabled = RefreshModelsButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        try
        {
            var url = ServerUrl.Text?.Trim() ?? "";
            Settings.ValidateServerUrl(url);
            Status.Text = "Loading models from Easy Diffusion...";
            using var easy = new EasyDiffusionClient(url);
            var catalog = await easy.GetModelsAsync(job.Token);
            if (!string.Equals(url, ServerUrl.Text?.Trim(), StringComparison.Ordinal))
            {
                Status.Text = "The Easy Diffusion URL changed; refresh the model lists for the new server.";
                return;
            }
            UpdateModelChoices(Model, catalog.Diffusion);
            UpdateModelChoices(Vae, catalog.Vae);
            EncoderChoices.ItemsSource = catalog.TextEncoders;
            UpdateModelChoices(UpscaleModel, catalog.Upscalers);
            Status.Text = $"Loaded {catalog.Diffusion.Length} diffusion models, {catalog.Vae.Length} VAEs, {catalog.TextEncoders.Length} text encoders, and {catalog.Upscalers.Length} upscalers. Some server entries may be download suggestions.";
        }
        catch (OperationCanceledException) { Status.Text = "Model refresh cancelled or timed out. Existing entries are unchanged."; }
        catch (Exception ex) { Status.Text = $"Could not load model lists: {ex.Message}"; }
        finally
        {
            job.Dispose(); job = null;
            FillButton.IsEnabled = UpscaleButton.IsEnabled = RefreshModelsButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }
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
        if (string.IsNullOrEmpty(prompt)) throw new ArgumentException("Enter a prompt describing what to create, change, or remove.");
        Status.Text = "Reading PhotoCraft document...";
        using var pc = await PhotoCraftClient.ConnectAsync(s, ct);
        var doc = await pc.InspectAsync(ct);
        var original = await pc.ExportAsync(s.ExchangePath, ct);
        var prepared = ImageTools.PrepareFill(original, doc, s.FillMegapixels, s.FillPixelStep, s.OptimizeSelection);
        using var easy = new EasyDiffusionClient(s.ServerUrl);
        Status.Text = $"Generating {(prepared.Mask is null ? "edit" : "fill")} (requested {prepared.Width} x {prepared.Height}) in Easy Diffusion...";
        var generated = await easy.FillAsync(s, prompt, negative, prepared.Source, prepared.Mask, prepared.Width, prepared.Height,
            new Progress<string>(message => Status.Text = message), ct);
        generated = ImageTools.RestoreFill(generated, doc, prepared);
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
        Status.Text = prepared.Mask is null ? "Placing edited layer in PhotoCraft..." : "Placing masked layer in PhotoCraft...";
        CancelButton.IsEnabled = false;
        try { await pc.PlaceAsync(name, doc, CancellationToken.None); }
        catch (Exception ex) { throw new InvalidOperationException($"Placement failed. Result saved at {path}. PhotoCraft may contain partial changes: {ex.Message}", ex); }
        File.Delete(path);
        Status.Text = prepared.Mask is null ? "Edit added as a new layer." : "Fill added as a masked layer.";
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
        FillButton.IsEnabled = UpscaleButton.IsEnabled = RefreshModelsButton.IsEnabled = false; CancelButton.IsEnabled = true;
        try { await action(ReadSettings(), job.Token); }
        catch (OperationCanceledException) when (job.IsCancellationRequested) { Status.Text = "Cancelled locally. Easy Diffusion may still finish its queued job."; }
        catch (OperationCanceledException) { Status.Text = "The request timed out. Easy Diffusion may still finish its queued job."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { job.Dispose(); job = null; FillButton.IsEnabled = UpscaleButton.IsEnabled = RefreshModelsButton.IsEnabled = true; CancelButton.IsEnabled = false; }
    }
}
