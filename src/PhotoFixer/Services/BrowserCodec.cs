using Microsoft.JSInterop;
using PhotoFixer.Core;

namespace PhotoFixer.Services;

/// <summary>ICodecSession over the browser's own codecs (see wwwroot/js/app.js).</summary>
public sealed class BrowserCodecShamil(IJSRuntime js) : ICodecSession
{
    public async Task RenderAsync(Geometry g) =>
        await js.InvokeVoidAsync("denelliPhotoFixer.render", g.CropX, g.CropY, g.CropW, g.CropH, g.OutW, g.OutH);

    public async Task<long> EncodeJpegAsync(int quality) =>
        await js.InvokeAsync<long>("denelliPhotoFixer.encodeJpeg", quality / 100.0);

    public async Task<long> EncodePngAsync() =>
        await js.InvokeAsync<long>("denelliPhotoFixer.encodePng");

    public async Task<byte[]> LastBytesAsync() =>
        await js.InvokeAsync<byte[]>("denelliPhotoFixer.lastBytes");
}

/// <summary>
/// ICodecSession for PDF sources (see wwwroot/js/app.js): pages are rasterised
/// once at a base scale; RenderAsync sets the downscale fraction and
/// EncodeJpegAsync rebuilds the whole PDF with page images at that scale and
/// quality, returning the PDF's byte size.
/// </summary>
public sealed class BrowserCodecPdf(IJSRuntime js, int baseWidth) : ICodecSession
{
    public async Task RenderAsync(Geometry g) =>
        await js.InvokeVoidAsync("denelliPhotoFixer.pdfRender", g.OutW / (double)baseWidth);

    public async Task<long> EncodeJpegAsync(int quality) =>
        await js.InvokeAsync<long>("denelliPhotoFixer.pdfBuild", quality / 100.0);

    public Task<long> EncodePngAsync() => throw new NotSupportedException("PDF output is always a rebuilt PDF.");

    public async Task<byte[]> LastBytesAsync() =>
        await js.InvokeAsync<byte[]>("denelliPhotoFixer.lastBytes");
}
