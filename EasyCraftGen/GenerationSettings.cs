using System.Text.Json.Serialization;

namespace EasyCraftGen;

public sealed record GenerationSettings(
    [property: JsonRequired] string Model,
    [property: JsonRequired] string Sampler,
    [property: JsonRequired] string Scheduler,
    [property: JsonRequired] string Vae,
    [property: JsonRequired] List<string> TextEncoders,
    [property: JsonRequired] string UpscaleModel,
    [property: JsonRequired] int Steps,
    [property: JsonRequired] double Guidance,
    [property: JsonRequired] int FillMegapixels,
    [property: JsonRequired] int FillPixelStep,
    [property: JsonRequired] bool OptimizeSelection,
    [property: JsonRequired] int Scale)
{
    public static GenerationSettings From(Settings settings) => new(
        settings.Model, settings.Sampler, settings.Scheduler, settings.Vae, [.. settings.TextEncoders],
        settings.UpscaleModel, settings.Steps, settings.Guidance, settings.FillMegapixels,
        settings.FillPixelStep, settings.OptimizeSelection, settings.Scale);

    public void ApplyTo(Settings settings)
    {
        if (Model is null || Sampler is null || Scheduler is null || Vae is null ||
            TextEncoders is null || UpscaleModel is null)
            throw new InvalidDataException("Generation settings contain null fields.");
        settings.Model = Model;
        settings.Sampler = Sampler;
        settings.Scheduler = Scheduler;
        settings.Vae = Vae;
        settings.TextEncoders = TextEncoders;
        settings.UpscaleModel = UpscaleModel;
        settings.Steps = Steps;
        settings.Guidance = Guidance;
        settings.FillMegapixels = FillMegapixels;
        settings.FillPixelStep = FillPixelStep;
        settings.OptimizeSelection = OptimizeSelection;
        settings.Scale = Scale;
    }
}
