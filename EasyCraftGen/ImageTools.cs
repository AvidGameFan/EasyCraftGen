using SkiaSharp;

namespace EasyCraftGen;

public static class ImageTools
{
    public static (byte[] Source, byte[]? Mask, int Width, int Height) PrepareFill(byte[] png, DocumentInfo doc, int megapixels, int pixelStep = 64)
    {
        if (megapixels is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(megapixels), "Choose 1, 2, 4, or 8 megapixels.");
        if (pixelStep is not (1 or 16 or 64))
            throw new ArgumentOutOfRangeException(nameof(pixelStep), "Choose a pixel step of 1, 16, or 64.");
        var s = doc.Selection;
        if (s is not null && (s.Length != 4 || s[2] <= 0 || s[3] <= 0))
            throw new InvalidOperationException("PhotoCraft returned invalid selection bounds.");
        using var source = SKBitmap.Decode(png) ?? throw new InvalidDataException("PhotoCraft did not export a valid image.");
        if (source.Width != doc.Width || source.Height != doc.Height) throw new InvalidDataException("Export dimensions do not match the document.");
        double scale = Math.Min(1, Math.Sqrt(megapixels * 1_000_000.0 / (doc.Width * (double)doc.Height)));
        int width = Math.Max(pixelStep, (int)Math.Round(doc.Width * scale / pixelStep) * pixelStep);
        int height = Math.Max(pixelStep, (int)Math.Round(doc.Height * scale / pixelStep) * pixelStep);
        using var image = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, new SKRect(0, 0, width, height));
        }
        if (s is null) return (Encode(image), null, width, height);
        var left = Math.Clamp(s[0], 0, doc.Width);
        var top = Math.Clamp(s[1], 0, doc.Height);
        var right = (int)Math.Clamp((long)s[0] + s[2], 0, doc.Width);
        var bottom = (int)Math.Clamp((long)s[1] + s[3], 0, doc.Height);
        if (right <= left || bottom <= top) throw new InvalidOperationException("The selection is outside the canvas.");
        using var mask = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.White })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawRect(new SKRect(left * (float)width / doc.Width, top * (float)height / doc.Height,
                right * (float)width / doc.Width, bottom * (float)height / doc.Height), paint);
        }
        return (Encode(image), Encode(mask), width, height);
    }

    public static byte[] Resize(byte[] png, int width, int height)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Easy Diffusion returned an invalid image.");
        using var result = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(result)) canvas.DrawBitmap(image, new SKRect(0, 0, width, height));
        return Encode(result);
    }

    public static (int Width, int Height) Dimensions(byte[] png)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Easy Diffusion returned an invalid image.");
        return (image.Width, image.Height);
    }

    private static byte[] Encode(SKBitmap image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
