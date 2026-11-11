using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace EasyCraftGen;

public sealed class EasyDiffusionClient : IDisposable
{
    private readonly HttpClient http;

    public EasyDiffusionClient(string url, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task TestAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("ping", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ModelCatalog> GetModelsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("get/models", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Easy Diffusion model list returned {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Easy Diffusion returned an invalid model list.");
        var diffusion = new List<string>();
        var vae = new List<string>();
        var encoders = new List<string>();
        var upscalers = new List<string>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("model", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
                !model.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Easy Diffusion returned a malformed model entry.");
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Easy Diffusion returned a malformed model tag.");
                var target = tag.GetString() switch
                {
                    "stable-diffusion" => diffusion,
                    "vae" => vae,
                    "text-encoder" => encoders,
                    "realesrgan" => upscalers,
                    _ => null
                };
                target?.Add(id.GetString()!);
            }
        }
        static string[] Sorted(List<string> values) => values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ModelCatalog(Sorted(diffusion), Sorted(vae), Sorted(encoders), Sorted(upscalers));
    }

    public Task<byte[]> FillAsync(Settings s, string prompt, string negative, byte[] image, byte[]? mask, int width, int height, IProgress<string> progress, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt, ["negative_prompt"] = negative, ["width"] = width, ["height"] = height,
            ["init_image"] = DataUrl(image), ["prompt_strength"] = 0.8,
            ["num_outputs"] = 1, ["num_inference_steps"] = s.Steps, ["guidance_scale"] = s.Guidance,
            ["seed"] = Random.Shared.Next(1, int.MaxValue), ["sampler_name"] = s.Sampler, ["scheduler_name"] = s.Scheduler,
            ["use_stable_diffusion_model"] = s.Model, ["use_vae_model"] = s.Vae,
            ["use_text_encoder_model"] = s.TextEncoders.Count == 0 ? null : s.TextEncoders.ToArray(),
            ["enable_vae_tiling"] = true, ["vram_usage_level"] = "low", ["output_format"] = "png",
            ["stream_progress_updates"] = true, ["stream_image_progress"] = false,
            ["session_id"] = Guid.NewGuid().ToString("N")
        };
        if (mask is not null) payload["mask"] = DataUrl(mask);
        return RunAsync("render", payload, progress, ct);
    }

    public Task<byte[]> UpscaleAsync(Settings s, byte[] image, IProgress<string> progress, CancellationToken ct) =>
        RunAsync("filter", new
        {
            image = DataUrl(image), filter = "realesrgan",
            filter_params = new { realesrgan = new { scale = s.Scale } },
            model_paths = new Dictionary<string, string> { ["realesrgan"] = s.UpscaleModel },
            output_format = "png", session_id = Guid.NewGuid().ToString("N")
        }, progress, ct);

    private async Task<byte[]> RunAsync(string endpoint, object payload, IProgress<string> progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await http.PostAsJsonAsync(endpoint, payload, timeout.Token);
        string body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Easy Diffusion {endpoint} returned {(int)response.StatusCode}: {body}");
        using var initial = JsonDocument.Parse(body);
        if (TryResult(initial.RootElement, out var direct)) return direct;
        if (!initial.RootElement.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Easy Diffusion did not provide a result or stream.");
        var uri = new Uri(http.BaseAddress!, stream.GetString());
        if (uri.Scheme != http.BaseAddress!.Scheme || uri.Authority != http.BaseAddress.Authority)
            throw new InvalidDataException("Easy Diffusion returned a stream on a different server.");
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var poll = await http.GetAsync(uri, timeout.Token);
            if (poll.StatusCode == (HttpStatusCode)425) { await Task.Delay(1000, timeout.Token); continue; }
            body = await poll.Content.ReadAsStringAsync(timeout.Token);
            if (!poll.IsSuccessStatusCode) throw new HttpRequestException($"Easy Diffusion stream returned {(int)poll.StatusCode}: {body}");
            foreach (var item in ParseObjects(body))
            {
                if (TryResult(item, out var result)) return result;
                if (item.TryGetProperty("step", out var step) && item.TryGetProperty("total_steps", out var total))
                    progress.Report($"Generating: step {step} of {total}...");
            }

            await Task.Delay(1000, timeout.Token);
        }
    }

    public static IEnumerable<JsonElement> ParseObjects(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var offset = 0;
        var results = new List<JsonElement>();
        while (offset < bytes.Length)
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(offset), true, default);
            if (!reader.Read()) break;
            using var json = JsonDocument.ParseValue(ref reader);
            results.Add(json.RootElement.Clone());
            offset += checked((int)reader.BytesConsumed);
        }
        return results;
    }

    private static bool TryResult(JsonElement root, out byte[] result)
    {
        result = [];
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unexpected Easy Diffusion response.");
        var hasStatus = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String;
        if (hasStatus && status.GetString() is "failed" or "error" or "stopped")
            throw new InvalidOperationException($"Easy Diffusion: {(root.TryGetProperty("detail", out var detail) ? detail.ToString() : status.ToString())}");
        if (root.TryGetProperty("step", out _) && (!hasStatus || status.GetString() != "succeeded")) return false;
        JsonElement outputs;
        if (!root.TryGetProperty("output", out outputs) && !root.TryGetProperty("images", out outputs))
        {
            if (hasStatus && status.GetString() == "succeeded") throw new InvalidDataException("Easy Diffusion completed without an image.");
            return false;
        }
        if (outputs.ValueKind != JsonValueKind.Array || outputs.GetArrayLength() == 0)
            throw new InvalidDataException("Easy Diffusion returned no images.");
        var first = outputs[0];
        if (first.ValueKind == JsonValueKind.Object) first = first.GetProperty("data");
        var value = first.GetString() ?? throw new InvalidDataException("Easy Diffusion returned empty image data.");
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Easy Diffusion returned an invalid image data URL.");
            value = value[(comma + 1)..];
        }
        result = Convert.FromBase64String(value);
        return true;
    }

    private static string DataUrl(byte[] bytes) => "data:image/png;base64," + Convert.ToBase64String(bytes);
    public void Dispose() => http.Dispose();
}

public sealed record ModelCatalog(string[] Diffusion, string[] Vae, string[] TextEncoders, string[] Upscalers);
