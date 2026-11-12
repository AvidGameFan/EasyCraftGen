namespace EasyCraftGen;

public static class FillGeneration
{
    public static async Task<byte[]> GenerateAsync(EasyDiffusionClient easy, Settings settings, string prompt, string negative,
        byte[] source, DocumentInfo document, Action<ImageTools.PreparedFill> preparedCallback,
        IProgress<string> progress, CancellationToken ct)
    {
        var prepared = ImageTools.PrepareFill(source, document, settings.FillMegapixels, settings.FillPixelStep, settings.OptimizeSelection);
        preparedCallback(prepared);
        var generated = await easy.FillAsync(settings, prompt, negative, prepared.Source, prepared.Mask, prepared.Width, prepared.Height, progress, ct);
        ct.ThrowIfCancellationRequested();
        return ImageTools.RestoreFill(generated, document, prepared);
    }
}
