using HoleLauncherBackend.Contracts;
using SkiaSharp;

namespace HoleLauncherBackend.Api;

internal static class GameTextures
{
    public const int MaxBytes = StorageAddress.MaxOfferImageBytes;
    public const int MaxInputBytes = 8 * 1024 * 1024;
    private const int MaxTextureDimension = 1024;
    private const int TextureWidth = 64;
    private const int SkinHeight = 64;
    private const int LegacySkinHeight = 32;
    private const int CapeHeight = 32;

    public static async Task<MemoryStream?> ReadAsync(Stream source, CancellationToken cancellation, int maxBytes = MaxBytes)
    {
        var bytes = new byte[maxBytes + 1];
        var length = await source.ReadAtLeastAsync(bytes, bytes.Length, false, cancellation);
        return length > maxBytes ? null : new MemoryStream(bytes, 0, length, writable: false, publiclyVisible: true);
    }

    public static MemoryStream? PrepareUpload(MemoryStream source, bool skin)
    {
        if (!source.TryGetBuffer(out var bytes))
            return null;

        using var data = SKData.CreateCopy(bytes.AsSpan());
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
            codec.Info.Width is < 1 or > MaxTextureDimension || codec.Info.Height is < 1 or > MaxTextureDimension)
            return null;

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        var legacy = skin && width == 2 * height;
        if (skin && (width < TextureWidth || width % TextureWidth != 0 || (!legacy && width != height)))
            return null;

        var targetHeight = skin && !legacy ? SkinHeight : CapeHeight;
        using var decoded = new SKBitmap(codec.Info);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            return null;
        var artwork = skin ? null : CapeArtwork.MapToTexture(width, height, (x, y) => (uint)decoded.GetPixel(x, y));
        if (artwork is not null)
        {
            using var mapped = new SKBitmap(CapeArtwork.CanvasWidth, CapeArtwork.CanvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            for (var y = 0; y < mapped.Height; y++)
                for (var x = 0; x < mapped.Width; x++)
                    mapped.SetPixel(x, y, new SKColor(artwork[y * mapped.Width + x]));
            return EncodePng(mapped);
        }
        if (width == TextureWidth && height == targetHeight && source.Length <= MaxBytes)
            return source;

        using var normalized = new SKBitmap(TextureWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(normalized);
        canvas.Clear(SKColors.Transparent);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest);
        var scale = skin ? (float)TextureWidth / width :
            Math.Min(1f, Math.Min((float)TextureWidth / width, (float)targetHeight / height));
        var destination = SKRect.Create(Math.Max(1, (int)MathF.Round(width * scale)),
            Math.Max(1, (int)MathF.Round(height * scale)));
        canvas.DrawBitmap(decoded, destination, sampling);
        canvas.Flush();
        return EncodePng(normalized);
    }

    private static MemoryStream? EncodePng(SKBitmap bitmap)
    {
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (png is null)
            return null;
        return new MemoryStream(png.ToArray(), writable: false);
    }


    public static bool IsValid(MemoryStream png, bool skin, string skinModel = SkinModels.Classic)
    {
        if (!png.TryGetBuffer(out var buffer))
            return false;

        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!buffer.AsSpan().StartsWith(signature))
            return false;

        using var data = SKData.CreateCopy(buffer.AsSpan());
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
            codec.Info.Width != TextureWidth ||
            (skin ? (codec.Info.Height != SkinHeight && (skinModel == SkinModels.Slim || codec.Info.Height != LegacySkinHeight)) : codec.Info.Height != CapeHeight))
            return false;

        using var pixels = new SKBitmap(codec.Info);
        return codec.GetPixels(pixels.Info, pixels.GetPixels()) == SKCodecResult.Success;
    }
}
