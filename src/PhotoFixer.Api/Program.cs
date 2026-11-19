using PhotoFixer.Api;
using PhotoFixer.Core;

var builder = WebApplication.CreateBuilder(args);
// Hard request-size ceiling (the handler also enforces 25 MB with a clear error).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 26L * 1024 * 1024);
var app = builder.Build();

// Same code serves both variants: keyless for self-host, key-gated when
// PhotoFixer:RequireApiKey=true for a hosted/shared deployment (code-ready,
// not deployed anywhere).
var gate = new ApiKeyGate(app.Configuration);

app.MapGet("/", () => Results.Text(
    "PhotoFixer self-host API (private build - unpublished).\n" +
    "POST /v1/process  multipart/form-data: file + optional targetWidth, targetHeight, maxKb, mode, format, preset\n" +
    "GET  /v1/presets  named size presets\n" +
    "GET  /healthz"));

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/v1/presets", () => Results.Json(PresetTable.PublicList));

app.MapPost("/v1/process", async (HttpRequest http) =>
{
    if (gate.Check(http) is { } denied)
        return Problem(denied.Status, denied.Message);

    if (!http.HasFormContentType)
        return Problem(400, "Send the photo as multipart/form-data in a 'file' field.");

    var form = await http.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Problem(400, "Attach the photo as form field 'file'.");
    if (file.Length > 25 * 1024 * 1024)
        return Problem(400, "Photo is larger than the 25 MB upload limit.");

    var bytes = new byte[file.Length];
    await using (var stream = file.OpenReadStream())
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        if (read != bytes.Length)
            return Problem(400, "Upload truncated; send the file again.");
    }

    var sniffed = ImageSniff.Sniff(bytes);
    if (sniffed is null)
        return Problem(415, "Unrecognized image format. Self-host v1 reads JPG, PNG, WebP, GIF and BMP.");
    if (sniffed is "image/heic")
        return Problem(422, "HEIC (iPhone) photos are not decoded by self-host v1 - the website handles those through the browser. Convert to JPEG or PNG first.");
    if (sniffed is "image/avif")
        return Problem(422, "AVIF is not decoded by self-host v1 - the website handles it through the browser. Convert to JPEG or PNG first.");

    if (!TryParseIntField(form["targetWidth"].ToString(), out var targetW) ||
        !TryParseIntField(form["targetHeight"].ToString(), out var targetH) ||
        !TryParseIntField(form["maxKb"].ToString(), out var maxKb))
        return Problem(400, "targetWidth, targetHeight and maxKb must be whole numbers.");

    FitMode? mode = null;
    if (form["mode"].Count > 0 && !string.IsNullOrWhiteSpace(form["mode"].ToString()))
    {
        mode = ParseMode(form["mode"].ToString(), out var modeErr);
        if (modeErr is not null) return Problem(400, modeErr);
    }

    OutputFormat? format = null;
    if (form["format"].Count > 0 && !string.IsNullOrWhiteSpace(form["format"].ToString()))
    {
        format = ParseFormat(form["format"].ToString(), out var fmtErr);
        if (fmtErr is not null) return Problem(400, fmtErr);
    }

    var presetKey = form["preset"].ToString();
    if (!string.IsNullOrEmpty(presetKey))
    {
        var preset = PresetTable.Find(presetKey);
        if (preset is null)
            return Problem(400, $"Unknown preset '{presetKey}'. See GET /v1/presets.");
        targetW ??= preset.Width;
        targetH ??= preset.Height;
        mode ??= FitMode.FillCrop; // same as the website: a preset asks for an exact size
    }

    var request = new ProcessRequest(targetW, targetH, maxKb,
        mode ?? FitMode.FitInside, format ?? OutputFormat.KeepOriginal);

    SkiaCodecSession codec;
    SourceImage src;
    try
    {
        codec = SkiaCodecSession.Create(bytes, sniffed, file.Length, out src);
    }
    catch (Exception ex)
    {
        return Problem(422, ex is InvalidDataException ? ex.Message : "The server could not decode this image (corrupt file or unsupported variant).");
    }

    // Bounded concurrency: image work is CPU/RAM heavy, so only a few requests
    // process at once; the rest wait briefly, then get an honest busy answer.
    if (!await ProcessingSlots.Gate.WaitAsync(TimeSpan.FromSeconds(15)))
        return Problem(503, "Busy processing other photos right now; retry in a few seconds.");
    try
    {
    using (codec)
    {
        ProcessResult result;
        try
        {
            result = await PhotoFixerEngine.ProcessAsync(src, request, codec);
        }
        catch (Exception)
        {
            return Problem(500, "Processing failed unexpectedly on this image. Nothing was stored.");
        }
        if (!result.Success)
            return Problem(400, result.Error!);

        var res = http.HttpContext.Response;
        res.Headers["X-PhotoFixer-Width"] = result.OutputWidth.ToString();
        res.Headers["X-PhotoFixer-Height"] = result.OutputHeight.ToString();
        res.Headers["X-PhotoFixer-Kb"] = result.OutputKb.ToString();
        if (result.JpegQuality is { } q)
            res.Headers["X-PhotoFixer-Jpeg-Quality"] = q.ToString();
        if (result.Notes.Count > 0)
            res.Headers["X-PhotoFixer-Notes"] = string.Join(" | ", result.Notes);
        if (result.Warnings.Count > 0)
            res.Headers["X-PhotoFixer-Warnings"] = string.Join(" | ", result.Warnings);

        return Results.File(result.OutputBytes!, result.MimeType!,
            $"photofixer-{result.OutputWidth}x{result.OutputHeight}{result.FileExtension}");
        }
    }
    finally
    {
        ProcessingSlots.Gate.Release();
    }
});

app.Run();

static IResult Problem(int status, string message) =>
    Results.Json(new { error = message }, statusCode: status);

static bool TryParseIntField(string v, out int? value)
{
    value = null;
    if (string.IsNullOrWhiteSpace(v)) return true;
    if (int.TryParse(v, out var n)) { value = n; return true; }
    return false;
}

static FitMode? ParseMode(string v, out string? error)
{
    error = null;
    switch (v.Trim().ToLowerInvariant())
    {
        case "fit-inside" or "fit_inside" or "fitinside": return FitMode.FitInside;
        case "fill-crop" or "fill_crop" or "fillcrop": return FitMode.FillCrop;
        case "stretch": return FitMode.Stretch;
        default:
            error = "mode must be one of: fit-inside, fill-crop, stretch.";
            return null;
    }
}

static OutputFormat? ParseFormat(string v, out string? error)
{
    error = null;
    switch (v.Trim().ToLowerInvariant())
    {
        case "auto" or "keep" or "original" or "keep-original": return OutputFormat.KeepOriginal;
        case "jpeg" or "jpg": return OutputFormat.Jpeg;
        case "png": return OutputFormat.Png;
        default:
            error = "format must be one of: auto, jpeg, png.";
            return null;
    }
}

static class ProcessingSlots
{
    public static readonly SemaphoreSlim Gate = new(4);
}
