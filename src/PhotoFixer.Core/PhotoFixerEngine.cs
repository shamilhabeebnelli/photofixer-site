namespace PhotoFixer.Core;

/// <summary>
/// All PhotoFixer decisions in pure C#: input validation, geometry,
/// JPEG quality search, when shrinking is allowed, and when to fail
/// honestly instead of silently changing what was asked for.
/// </summary>
public static class PhotoFixerEngine
{
    public const int MinDimension = 16;
    public const int MaxDimension = 10000;
    public const int MinKb = 5;
    public const int MaxKb = 20480;
    public const int MinQuality = 5;
    public const int MaxQuality = 95;
    public const int DefaultQuality = 90;

    public static string? Validate(SourceImage? src, ProcessRequest req)
    {
        if (src is null) return "Choose a photo first (JPG, PNG, WebP, GIF, BMP, AVIF or HEIC).";
        if (req.TargetWidth is null && req.TargetHeight is null && req.MaxKb is null)
            return "Enter a maximum size in KB first.";
        if (req.TargetWidth is < MinDimension or > MaxDimension || req.TargetHeight is < MinDimension or > MaxDimension)
            return $"Dimensions must be between {MinDimension} and {MaxDimension} pixels.";
        if (req.MaxKb is < MinKb or > MaxKb)
            return $"Max size must be between {MinKb} KB and {MaxKb} KB.";
        if (req.Mode is FitMode.FillCrop or FitMode.Stretch && (req.TargetWidth is null || req.TargetHeight is null))
            return "This resize mode needs both a target width and a target height.";
        return null;
    }

    public static async Task<ProcessResult> ProcessAsync(SourceImage src, ProcessRequest req, ICodecSession codec)
    {
        var notes = new List<string>();
        var warnings = new List<string>();

        if (Validate(src, req) is { } err)
            return ProcessResult.Fail(err);

        var geo = GeometrySolver.Solve(src.Width, src.Height, req.Mode, req.TargetWidth, req.TargetHeight);
        var exactDims = req.Mode is FitMode.FillCrop or FitMode.Stretch;

        if ((req.TargetWidth is { } tw && tw > src.Width) || (req.TargetHeight is { } th && th > src.Height))
            warnings.Add("The target is larger than the original in at least one dimension. Upscaling can look soft or blurry.");
        if (req.Mode == FitMode.FillCrop)
            notes.Add($"Cropped from the center and resized to exactly {geo.OutW} x {geo.OutH} px. Edges of the photo may be cut off.");
        if (req.Mode == FitMode.Stretch)
            warnings.Add("Stretch mode ignores the original proportions, so the photo may look squeezed.");
        notes.Add("Camera and location metadata (EXIF) is removed: the result is re-encoded from pixels only.");

        var format = req.Format switch
        {
            OutputFormat.Jpeg => OutputFormat.Jpeg,
            OutputFormat.Png => OutputFormat.Png,
            _ => src.Format.Contains("png", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Png : OutputFormat.Jpeg
        };

        long? limitBytes = req.MaxKb is { } kb ? kb * 1000L : null;
        await codec.RenderAsync(geo);

        int? quality = null;
        long size;
        string mime;
        string ext;

        if (format == OutputFormat.Jpeg && !src.Format.Contains("jpeg", StringComparison.OrdinalIgnoreCase) && !src.Format.Contains("jpg", StringComparison.OrdinalIgnoreCase))
            notes.Add($"Converted from {FormatName(src.Format)} to JPEG - JPEG is the format that can reliably honour a strict KB limit.");

        if (format == OutputFormat.Png)
        {
            size = await codec.EncodePngAsync();
            mime = "image/png"; ext = ".png";
            if (limitBytes is { } pl && size > pl)
                // A KB limit is a strict ceiling: never present an over-limit file as a success.
                return ProcessResult.Fail(
                    $"PNG is lossless and cannot reach {req.MaxKb} KB here; this image comes out at {Round(size)} KB as PNG. Choose JPEG output, which can be compressed to the target.");
            else
                notes.Add("PNG keeps full quality, but file sizes stay larger than JPEG.");
        }
        else
        {
            mime = "image/jpeg"; ext = ".jpg";
            if (limitBytes is { } jl)
            {
                var solved = await SolveQualityAsync(codec, jl);
                if (solved is null)
                {
                    if (exactDims)
                    {
                        // Exact pixel dimensions are a hard requirement: never shrink them.
                        var smallest = await codec.EncodeJpegAsync(MinQuality);
                        return ProcessResult.Fail(
                            $"Cannot reach {req.MaxKb} KB at exactly {geo.OutW} x {geo.OutH} px. The smallest practical file at that exact size is about {Round(smallest)} KB. " +
                            "Raise the max KB, or switch to \"Fit inside\" or smaller dimensions.");
                    }

                    // Fit inside: the entered dimensions are upper bounds, so going
                    // smaller is allowed, but it is always reported.
                    var shrunk = false;
                    for (var i = 0; i < 10 && solved is null; i++)
                    {
                        var nw = Math.Max(16, (int)(geo.OutW * 0.85));
                        var nh = Math.Max(16, (int)(geo.OutH * 0.85));
                        if (nw == geo.OutW && nh == geo.OutH) break;
                        geo = geo with { OutW = nw, OutH = nh };
                        await codec.RenderAsync(geo);
                        shrunk = true;
                        solved = await SolveQualityAsync(codec, jl);
                    }
                    if (solved is null)
                    {
                        var smallest = await codec.EncodeJpegAsync(MinQuality);
                        return ProcessResult.Fail(
                            $"Cannot reach {req.MaxKb} KB even after scaling down to {geo.OutW} x {geo.OutH} px (smallest practical size about {Round(smallest)} KB). Raise the max KB.");
                    }
                    quality = solved;
                    if (shrunk)
                        notes.Add($"Resolution reduced to {geo.OutW} x {geo.OutH} px (proportions kept) because quality alone could not reach the {req.MaxKb} KB target. Scaled down, never cropped or stretched.");
                }
                else
                {
                    quality = solved;
                }
                size = await codec.EncodeJpegAsync(quality.Value); // leave the chosen quality as "last"
                if (size > jl)
                    // Final acceptance check: never return an over-limit file as a success, even if an
                    // encoder ever turns out non-deterministic. The KB limit is a strict ceiling.
                    return ProcessResult.Fail(
                        $"Cannot reliably reach {req.MaxKb} KB for this photo (smallest verified result was {Round(size)} KB). Raise the max KB.");
            }
            else
            {
                quality = DefaultQuality;
                size = await codec.EncodeJpegAsync(quality.Value);
            }

            if (quality is < 60 and >= 40)
                warnings.Add($"JPEG quality {quality} is fairly low. Zoom in on the preview to check faces and text before using it.");
            else if (quality is < 40)
                warnings.Add($"JPEG quality {quality} is very low. Expect visible blockiness. A bigger max KB or smaller dimensions would look better.");
        }

        var bytes = await codec.LastBytesAsync();
        var fmtName = format == OutputFormat.Png ? "PNG" : "JPEG";
        notes.Add($"Result: {geo.OutW} x {geo.OutH} px, {Round(size)} KB, {fmtName}{(quality is { } q ? $", quality {q}" : "")}.");
        return new ProcessResult(true, null, bytes, mime, ext, geo.OutW, geo.OutH, Round(size), quality, notes, warnings);
    }

    /// <summary>
    /// KB-only compression for PDFs. The caller's codec rasterises every page
    /// and rebuilds the PDF; this method makes the same decisions as the photo
    /// path: quality first, scale down second (always disclosed), a strict
    /// ceiling with a final acceptance check, and honest failure. A PDF that
    /// is already under the limit is returned unchanged - never inflated,
    /// never rasterised, text stays selectable.
    /// </summary>
    public static async Task<ProcessResult> ProcessPdfAsync(SourceImage src, ProcessRequest req, ICodecSession codec)
    {
        var notes = new List<string>();
        var warnings = new List<string>();

        if (req.MaxKb is < MinKb or > MaxKb)
            return ProcessResult.Fail($"Max size must be between {MinKb} KB and {MaxKb} KB.");
        long limit = req.MaxKb.Value * 1000L;
        var pages = Math.Max(1, src.Pages);

        if (src.Bytes <= limit)
        {
            notes.Add($"This PDF is already under {req.MaxKb} KB, so the original file is kept unchanged - text stays selectable and searchable.");
            var original = await codec.LastBytesAsync();
            return new ProcessResult(true, null, original, "application/pdf", ".pdf", src.Width, src.Height, Round(src.Bytes), null, notes, warnings, pages);
        }

        notes.Add(pages == 1
            ? "The page is re-rendered as an image and rebuilt into a new PDF: it looks the same, but text can no longer be selected or searched, and links, forms and annotations do not carry over."
            : $"All {pages} pages are re-rendered as images and rebuilt into a new PDF: they look the same, but text can no longer be selected or searched, and links, forms and annotations do not carry over.");

        var geo = new Geometry(0, 0, src.Width, src.Height, src.Width, src.Height);
        await codec.RenderAsync(geo);

        int? quality;
        long size;
        var solved = await SolveQualityAsync(codec, limit);
        if (solved is null)
        {
            var shrunk = false;
            for (var i = 0; i < 10 && solved is null; i++)
            {
                var nw = Math.Max(16, (int)(geo.OutW * 0.85));
                var nh = Math.Max(16, (int)(geo.OutH * 0.85));
                if (nw == geo.OutW && nh == geo.OutH) break;
                geo = geo with { OutW = nw, OutH = nh };
                await codec.RenderAsync(geo);
                shrunk = true;
                solved = await SolveQualityAsync(codec, limit);
            }
            if (solved is null)
            {
                var smallest = await codec.EncodeJpegAsync(MinQuality);
                return ProcessResult.Fail(
                    $"Cannot reach {req.MaxKb} KB for this {pages}-page PDF even after scaling page images down to {geo.OutW} x {geo.OutH} px (smallest practical size about {Round(smallest)} KB). Raise the max KB.");
            }
            quality = solved;
            if (shrunk)
                notes.Add($"Page resolution reduced to {geo.OutW} x {geo.OutH} px (proportions kept) because quality alone could not reach the {req.MaxKb} KB target. Scaled down, never cropped or stretched.");
        }
        else
        {
            quality = solved;
        }
        size = await codec.EncodeJpegAsync(quality.Value); // leave the chosen quality as "last"
        if (size > limit)
            // Final acceptance check: the KB limit is a strict ceiling, never an over-limit success.
            return ProcessResult.Fail(
                $"Cannot reliably reach {req.MaxKb} KB for this PDF (smallest verified result was {Round(size)} KB). Raise the max KB.");

        if (quality is < 60 and >= 40)
            warnings.Add($"Page-image quality {quality} is fairly low. Zoom in on the preview to check small text before using the result.");
        else if (quality is < 40)
            warnings.Add($"Page-image quality {quality} is very low. Expect visible blockiness in text. A bigger max KB would look better.");

        var bytes = await codec.LastBytesAsync();
        notes.Add($"Result: {pages} page(s), page images {geo.OutW} x {geo.OutH} px, {Round(size)} KB, PDF (page images at JPEG quality {quality}).");
        return new ProcessResult(true, null, bytes, "application/pdf", ".pdf", geo.OutW, geo.OutH, Round(size), quality, notes, warnings, pages);
    }

    /// <summary>Highest quality whose encoded size fits the limit; null when even the floor quality is too big.</summary>
    private static async Task<int?> SolveQualityAsync(ICodecSession codec, long limitBytes)
    {
        // Binary search; encoder size is monotonic in quality in practice.
        int lo = MinQuality, hi = MaxQuality;
        int? best = null;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var size = await codec.EncodeJpegAsync(mid);
            if (size <= limitBytes) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    private static double Round(long bytes) => Math.Round(bytes / 1000.0, 1);

    private static string FormatName(string mime) => mime switch
    {
        "image/png" => "PNG",
        "image/webp" => "WebP",
        "image/gif" => "GIF",
        "image/bmp" => "BMP",
        "image/avif" => "AVIF",
        "image/heic" => "HEIC",
        _ => "JPEG"
    };
}
