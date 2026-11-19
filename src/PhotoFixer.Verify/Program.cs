using PhotoFixer.Core;

// Console harness for the dependency-free C# engine. The fake codec models a
// monotonic encoder size curve; codec correctness itself is verified by the
// real-Chrome end-to-end test against the published site.

var failures = 0;

void Check(string name, bool cond, string detail)
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}  {detail}");
    if (!cond) failures++;
}

var src = new SourceImage(3000, 2000, "image/jpeg", 6_500_000);

// --- Geometry ---
var g1 = GeometrySolver.Solve(3000, 2000, FitMode.FillCrop, 600, 600);
Check("G1 fill crop rect", g1.CropX == 500 && g1.CropY == 0 && g1.CropW == 2000 && g1.CropH == 2000 && g1.OutW == 600 && g1.OutH == 600, g1.ToString());

var g2 = GeometrySolver.Solve(3000, 2000, FitMode.FitInside, 1000, null);
Check("G2 fit width only", g2.OutW == 1000 && g2.OutH == 667 && g2.CropW == 3000, g2.ToString());

var g3 = GeometrySolver.Solve(3000, 2000, FitMode.Stretch, 400, 800);
Check("G3 stretch exact", g3.OutW == 400 && g3.OutH == 800, g3.ToString());

var g4 = GeometrySolver.Solve(3000, 2000, FitMode.FitInside, null, null);
Check("G4 no dims identity", g4.OutW == 3000 && g4.OutH == 2000, g4.ToString());

// --- Exact dims: KB unreachable => fail, never shrink ---
{
    var codec = new FakeCodec(0.5); // 600x600 at q5 = 360000*0.5*0.242 = ~43.6KB > 5KB
    var r = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(600, 600, 5, FitMode.FillCrop, OutputFormat.Jpeg), codec);
    Check("E1 exact-dims fails", !r.Success, r.Error ?? "");
    Check("E1 error tells how to relax", (r.Error ?? "").Contains("Raise the max KB"), r.Error ?? "");
    Check("E1 never re-rendered (no shrink)", codec.RenderCount == 1, $"renders={codec.RenderCount}");
}

// --- Exact dims: KB reachable at some quality ---
{
    var codec = new FakeCodec(0.05);
    var r = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(600, 600, 200, FitMode.FillCrop, OutputFormat.KeepOriginal), codec);
    Check("E2 success", r.Success, r.Error ?? "");
    Check("E2 exact dims kept", r.OutputWidth == 600 && r.OutputHeight == 600, $"{r.OutputWidth}x{r.OutputHeight}");
    Check("E2 under limit", r.OutputKb <= 200, $"{r.OutputKb} KB");
    Check("E2 quality searched", r.JpegQuality is >= 5 and <= 95, $"q={r.JpegQuality}");
}

// --- Fit inside: allowed to shrink below the upper bounds, and says so ---
{
    var codec = new FakeCodec(0.5); // too big at 800x533 even at q5
    var r = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(800, null, 30, FitMode.FitInside, OutputFormat.Jpeg), codec);
    Check("E3 fit shrink succeeds", r.Success, r.Error ?? "");
    Check("E3 shrank below bound", r.OutputWidth < 800, $"{r.OutputWidth}x{r.OutputHeight}");
    Check("E3 shrink is disclosed", r.Notes.Any(n => n.Contains("Scaled down")), string.Join(" | ", r.Notes));
    Check("E3 under limit", r.OutputKb <= 30, $"{r.OutputKb} KB");
}

// --- Fit inside: impossible even after max shrink => fail clearly ---
{
    var codec = new FakeCodec(4.0);
    var r = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(800, null, 5, FitMode.FitInside, OutputFormat.Jpeg), codec);
    Check("E4 impossible fails", !r.Success, r.Error ?? "");
    Check("E4 fail mentions KB", (r.Error ?? "").Contains("Raise the max KB"), r.Error ?? "");
}

// --- PNG honesty: a KB limit is a strict ceiling, never an over-limit success ---
{
    var codec = new FakeCodec(0.05);
    var r = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(400, null, 50, FitMode.FitInside, OutputFormat.Png), codec);
    Check("P1 png over-limit fails", !r.Success, r.Error ?? "");
    Check("P1 fail names PNG + JPEG route", (r.Error ?? "").Contains("PNG") && (r.Error ?? "").Contains("JPEG"), r.Error ?? "");

    var ok = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(400, null, 500, FitMode.FitInside, OutputFormat.Png), new FakeCodec(0.05));
    Check("P2 png under limit succeeds", ok.Success && ok.MimeType == "image/png" && ok.OutputKb <= 500, $"{ok.OutputKb} KB");
}

// --- Validation ---
{
    var codec = new FakeCodec(0.05);
    var noTargets = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(null, null, null, FitMode.FitInside, OutputFormat.KeepOriginal), codec);
    Check("V1 no targets rejected", !noTargets.Success, noTargets.Error ?? "");

    var fillOneDim = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(600, null, null, FitMode.FillCrop, OutputFormat.Jpeg), codec);
    Check("V2 fill needs both dims", !fillOneDim.Success, fillOneDim.Error ?? "");

    var badKb = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(null, null, 2, FitMode.FitInside, OutputFormat.Jpeg), codec);
    Check("V3 kb range rejected", !badKb.Success, badKb.Error ?? "");

    var badDim = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(4, null, null, FitMode.FitInside, OutputFormat.Jpeg), codec);
    Check("V4 dim range rejected", !badDim.Success, badDim.Error ?? "");
}

// --- Upscale + low-quality warnings ---
{
    var codec = new FakeCodec(0.05);
    var up = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(4000, null, null, FitMode.FitInside, OutputFormat.Jpeg), codec);
    Check("W1 upscale warning", up.Warnings.Any(w => w.Contains("Upscaling")), string.Join(" | ", up.Warnings));

    var lqCodec = new FakeCodec(0.2); // lands at quality ~32 for a 40 KB cap
    var lq = await PhotoFixerEngine.ProcessAsync(src, new ProcessRequest(800, null, 40, FitMode.FitInside, OutputFormat.Jpeg), lqCodec);
    Check("W2 low quality warning when squeezed", lq.Success && lq.Warnings.Any(w => w.Contains("quality")),
        $"q={lq.JpegQuality} " + string.Join(" | ", lq.Warnings));
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures;

sealed class FakeCodec(double bytesPerPixelAtQ95) : ICodecSession
{
    public int RenderCount;
    public Geometry Last;

    public Task RenderAsync(Geometry g) { RenderCount++; Last = g; return Task.CompletedTask; }

    public Task<long> EncodeJpegAsync(int quality)
    {
        var factor = 0.2 + 0.8 * quality / 95.0; // monotonic in quality
        return Task.FromResult((long)(Last.OutW * Last.OutH * bytesPerPixelAtQ95 * factor));
    }

    public Task<long> EncodePngAsync() => Task.FromResult((long)(Last.OutW * Last.OutH * 1.6));
    public Task<byte[]> LastBytesAsync() => Task.FromResult(new byte[] { 0xFF, 0xD8, 0xFF });
}
