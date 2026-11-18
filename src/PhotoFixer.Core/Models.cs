namespace PhotoFixer.Core;

public enum FitMode { FitInside, FillCrop, Stretch }
public enum OutputFormat { KeepOriginal, Jpeg, Png }

/// <summary>Facts about the uploaded source image.</summary>
public sealed record SourceImage(int Width, int Height, string Format, long Bytes);

/// <summary>Center-crop rectangle in source pixels plus the final output size.</summary>
public sealed record Geometry(int CropX, int CropY, int CropW, int CropH, int OutW, int OutH);

public sealed record ProcessRequest(int? TargetWidth, int? TargetHeight, int? MaxKb, FitMode Mode, OutputFormat Format);

public sealed record ProcessResult(
    bool Success,
    string? Error,
    byte[]? OutputBytes,
    string? MimeType,
    string? FileExtension,
    int OutputWidth,
    int OutputHeight,
    double OutputKb,
    int? JpegQuality,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings)
{
    public static ProcessResult Fail(string error) =>
        new(false, error, null, null, null, 0, 0, 0, null,
            Array.Empty<string>(), Array.Empty<string>());
}
