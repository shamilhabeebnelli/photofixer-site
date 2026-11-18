namespace PhotoFixer.Core;

public static class GeometrySolver
{
    public static Geometry Solve(int srcW, int srcH, FitMode mode, int? targetW, int? targetH)
    {
        if (mode is FitMode.FillCrop or FitMode.Stretch)
        {
            var w = targetW!.Value;
            var h = targetH!.Value;

            if (mode == FitMode.Stretch)
                return new Geometry(0, 0, srcW, srcH, w, h);

            // Largest center crop matching the target aspect ratio.
            double targetAspect = (double)w / h;
            double srcAspect = (double)srcW / srcH;
            int cw, ch;
            if (srcAspect > targetAspect) { ch = srcH; cw = (int)Math.Round(srcH * targetAspect); }
            else { cw = srcW; ch = (int)Math.Round(srcW / targetAspect); }
            cw = Math.Clamp(cw, 1, srcW);
            ch = Math.Clamp(ch, 1, srcH);
            return new Geometry((srcW - cw) / 2, (srcH - ch) / 2, cw, ch, w, h);
        }

        // FitInside: targets are upper bounds; the image keeps its proportions.
        double scale = double.PositiveInfinity;
        if (targetW is { } tw) scale = Math.Min(scale, (double)tw / srcW);
        if (targetH is { } th) scale = Math.Min(scale, (double)th / srcH);
        if (double.IsInfinity(scale)) scale = 1.0;
        var ow = Math.Max(1, (int)Math.Round(srcW * scale));
        var oh = Math.Max(1, (int)Math.Round(srcH * scale));
        return new Geometry(0, 0, srcW, srcH, ow, oh);
    }
}
