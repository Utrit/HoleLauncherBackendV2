namespace HoleLauncherBackend.Contracts;

public static class CapeArtwork
{
    public const int CanvasWidth = 64;
    public const int CanvasHeight = 32;
    private const int FaceWidth = 10;
    private const int FaceHeight = 16;
    private const int LayoutWidth = 22;
    private const int LayoutHeight = 17;
    private const int OuterFaceX = 1;
    private const int InnerFaceX = 12;
    private const int FaceY = 1;

    public static uint[]? MapToTexture(int width, int height, Func<int, int, uint> readPixel)
    {
        // Full-size and HD atlases have UVs of their own. Only inspect the native
        // size for artwork previously copied into the top-left corner.
        if (width > CanvasWidth && width % CanvasWidth == 0 && height == width / 2)
            return null;

        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if ((readPixel(x, y) >> 24) == 0)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || (width == CanvasWidth && height == CanvasHeight && right >= LayoutWidth) ||
            (width >= LayoutWidth && height >= LayoutHeight && right < LayoutWidth && bottom < LayoutHeight))
            return null;

        var pixels = new uint[CanvasWidth * CanvasHeight];
        var artworkWidth = right - left + 1;
        var artworkHeight = bottom - top + 1;
        for (var y = 0; y < FaceHeight; y++)
        {
            for (var x = 0; x < FaceWidth; x++)
            {
                var sourceX = left + (2 * x + 1) * artworkWidth / (2 * FaceWidth);
                var sourceY = top + (2 * y + 1) * artworkHeight / (2 * FaceHeight);
                var color = readPixel(sourceX, sourceY);
                var row = (FaceY + y) * CanvasWidth;
                pixels[row + OuterFaceX + x] = color;
                pixels[row + InnerFaceX + x] = color;
                if (x == 0)
                    pixels[row] = color;
                if (x == FaceWidth - 1)
                    pixels[row + OuterFaceX + FaceWidth] = color;
                if (y == 0)
                    pixels[OuterFaceX + x] = color;
                if (y == FaceHeight - 1)
                    pixels[OuterFaceX + FaceWidth + x] = color;
            }
        }
        return pixels;
    }
}
