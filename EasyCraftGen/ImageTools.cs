using SkiaSharp;

namespace EasyCraftGen;

public static class ImageTools
{
    public sealed record PreparedFill(byte[] Source, byte[]? Mask, int Width, int Height, SKRectI Crop, SKRect Content);

    public static PreparedFill PrepareFill(byte[] png, DocumentInfo doc, int megapixels, int pixelStep = 64, bool optimizeSelection = false)
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
        var selection = SKRectI.Empty;
        if (s is not null)
        {
            selection = new SKRectI(Math.Clamp(s[0], 0, doc.Width), Math.Clamp(s[1], 0, doc.Height),
                (int)Math.Clamp((long)s[0] + s[2], 0, doc.Width),
                (int)Math.Clamp((long)s[1] + s[3], 0, doc.Height));
            if (selection.IsEmpty) throw new InvalidOperationException("The selection is outside the canvas.");
        }
        var crop = new SKRectI(0, 0, doc.Width, doc.Height);
        if (optimizeSelection && s is not null)
        {
            var (left, right) = CropAxis(selection.Left, selection.Right, doc.Width);
            var (top, bottom) = CropAxis(selection.Top, selection.Bottom, doc.Height);
            crop = new SKRectI(left, top, right, bottom);
        }
        double scale = Math.Min(1, Math.Sqrt(megapixels * 1_000_000.0 / (crop.Width * (double)crop.Height)));
        int contentWidth = Math.Max(pixelStep, (int)Math.Round(crop.Width * scale / pixelStep) * pixelStep);
        int contentHeight = Math.Max(pixelStep, (int)Math.Round(crop.Height * scale / pixelStep) * pixelStep);
        int width = Math.Max(384, contentWidth);
        int height = Math.Max(384, contentHeight);
        int padLeft = (width - contentWidth) / 2;
        int padTop = (height - contentHeight) / 2;
        var content = new SKRect(padLeft, padTop, padLeft + contentWidth, padTop + contentHeight);
        using var image = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, crop, content);
        }
        if (s is null) return new PreparedFill(Encode(image), null, width, height, crop, content);
        using var mask = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(mask))
        using (var paint = new SKPaint { Color = SKColors.White })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawRect(new SKRect(padLeft + (selection.Left - crop.Left) * (float)contentWidth / crop.Width,
                padTop + (selection.Top - crop.Top) * (float)contentHeight / crop.Height,
                padLeft + (selection.Right - crop.Left) * (float)contentWidth / crop.Width,
                padTop + (selection.Bottom - crop.Top) * (float)contentHeight / crop.Height), paint);
        }
        return new PreparedFill(Encode(image), Encode(mask), width, height, crop, content);
    }

    private static (int Start, int End) CropAxis(int selectionStart, int selectionEnd, int limit)
    {
        int start = (int)Math.Max(0L, (long)selectionStart - 64);
        int end = (int)Math.Min(limit, (long)selectionEnd + 64);
        int extra = Math.Max(0, Math.Min(384, limit) - (end - start));
        start = Math.Max(0, start - extra / 2);
        end = Math.Min(limit, start + Math.Max(384, end - start));
        start = Math.Max(0, Math.Min(start, end - Math.Min(384, limit)));
        return (start, end);
    }

    public static byte[] RestoreFill(byte[] png, DocumentInfo doc, PreparedFill prepared)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Easy Diffusion returned an invalid image.");
        var content = new SKRect(prepared.Content.Left * image.Width / prepared.Width,
            prepared.Content.Top * image.Height / prepared.Height,
            prepared.Content.Right * image.Width / prepared.Width,
            prepared.Content.Bottom * image.Height / prepared.Height);
        using var result = new SKBitmap(doc.Width, doc.Height);
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(image, content, new SKRect(prepared.Crop.Left, prepared.Crop.Top, prepared.Crop.Right, prepared.Crop.Bottom));
        }
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
