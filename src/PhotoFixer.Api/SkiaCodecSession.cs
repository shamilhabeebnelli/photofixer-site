using SkiaSharp;
using PhotoFixer.Core;

namespace PhotoFixer.Api;

/// <summary>
/// ICodecSession over SkiaSharp (MIT) for the self-host server build.
/// Mirrors the website's browser codec: white background composite,
/// high-quality sampling, JPEG quality 5-95, PNG lossless.
/// Decode happens once per request; the decoded source is kept for the
/// engine's repeated render/encode passes. Everything is in memory;
/// nothing is written to disk and nothing is retained after the request.
/// </summary>
public sealed class SkiaCodecSession : ICodecSession, IDisposable
{
    private readonly SKBitmap _sourceBitmap;
    private SKBitmap? _working;
    private byte[]? _last;

    private SkiaCodecSession(SKBitmap source) => _sourceBitmap = source;

    // Input bounds are checked from the header BEFORE pixel allocation, so a
    // decompression bomb (tiny file, huge dimensions) is refused cheaply.
    public const int MaxSidePx = 16384;
    public const long MaxTotalPx = 50_000_000; // 50 MP

    public static SkiaCodecSession Create(byte[] bytes, string sniffedFormat, long byteCount, out SourceImage src)
    {
        using var ms = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(ms)
            ?? throw new InvalidDataException("SkiaSharp could not decode the image.");
        var w = codec.Info.Width;
        var h = codec.Info.Height;
        if (w > MaxSidePx || h > MaxSidePx || (long)w * h > MaxTotalPx)
            throw new InvalidDataException(
                $"This image is {w} x {h} px - too large for this service (limit {MaxTotalPx / 1_000_000} MP / {MaxSidePx} px per side).");
        var bmp = SKBitmap.Decode(codec)
            ?? throw new InvalidDataException("SkiaSharp could not decode the image.");
        src = new SourceImage(w, h, sniffedFormat, byteCount);
        return new SkiaCodecSession(bmp);
    }

    public Task RenderAsync(Geometry g)
    {
        _working?.Dispose();
        _working = new SKBitmap(g.OutW, g.OutH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(_working);
        canvas.Clear(SKColors.White); // JPEG has no alpha: composite on white like the browser build
        using var image = SKImage.FromBitmap(_sourceBitmap);
        var srcRect = new SKRect(g.CropX, g.CropY, g.CropX + g.CropW, g.CropY + g.CropH);
        var destRect = new SKRect(0, 0, g.OutW, g.OutH);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell); // high-quality smoothing
        canvas.DrawImage(image, srcRect, destRect, sampling);
        canvas.Flush();
        return Task.CompletedTask;
    }

    public Task<long> EncodeJpegAsync(int quality)
    {
        using var image = SKImage.FromBitmap(_working!);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        _last = data.ToArray();
        return Task.FromResult((long)_last.Length);
    }

    public Task<long> EncodePngAsync()
    {
        using var image = SKImage.FromBitmap(_working!);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _last = data.ToArray();
        return Task.FromResult((long)_last.Length);
    }

    public Task<byte[]> LastBytesAsync() => Task.FromResult(_last!);

    public void Dispose()
    {
        _working?.Dispose();
        _sourceBitmap.Dispose();
    }
}
