// PhotoFixer browser codec: pixel work only. All decisions are made in C#.
// Uses the browser's own maintained image codecs (createImageBitmap + canvas),
// so there is no third-party image library anywhere in the app.
window.photoFixer = {
    _bitmap: null,   // decoded source image (decoded once at upload)
    _canvas: null,   // OffscreenCanvas holding the crop+resize result
    _last: null,     // Blob of the most recent encode
    _previewUrl: null,
    _downloadUrl: null,

    // Decode once to read dimensions/format. Returns {width, height, type}.
    probe: async function (bytes, contentType) {
        var blob = new Blob([bytes], { type: contentType });
        var bmp = await createImageBitmap(blob);
        var res = { width: bmp.width, height: bmp.height, type: blob.type || "unknown" };
        bmp.close();
        return res;
    },

    // Keep the decoded source for repeated renders without re-uploading bytes.
    setSource: async function (bytes, contentType) {
        if (window.photoFixer._bitmap) window.photoFixer._bitmap.close();
        window.photoFixer._bitmap = await createImageBitmap(new Blob([bytes], { type: contentType }));
    },

    // Crop (cx,cy,cw,ch in source pixels) + resize to (ow,oh) onto the working canvas.
    render: function (cx, cy, cw, ch, ow, oh) {
        var bmp = window.photoFixer._bitmap;
        var canvas = new OffscreenCanvas(ow, oh);
        var ctx = canvas.getContext("2d");
        ctx.fillStyle = "#FFFFFF"; // JPEG has no alpha; composite on white, never black
        ctx.fillRect(0, 0, ow, oh);
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = "high";
        ctx.drawImage(bmp, cx, cy, cw, ch, 0, 0, ow, oh);
        window.photoFixer._canvas = canvas;
    },

    // Encode the working canvas as JPEG; quality is 0.05-0.95. Returns byte size.
    encodeJpeg: async function (quality) {
        var blob = await window.photoFixer._canvas.convertToBlob({ type: "image/jpeg", quality: quality });
        window.photoFixer._last = blob;
        return blob.size;
    },

    // Encode the working canvas as PNG. Returns byte size.
    encodePng: async function () {
        var blob = await window.photoFixer._canvas.convertToBlob({ type: "image/png" });
        window.photoFixer._last = blob;
        return blob.size;
    },

    // Bytes of the most recent encode (for the C# result record).
    lastBytes: async function () {
        return new Uint8Array(await window.photoFixer._last.arrayBuffer());
    },

    // Show the last encode in the preview <img>.
    setPreviewFromLast: function () {
        if (window.photoFixer._previewUrl) URL.revokeObjectURL(window.photoFixer._previewUrl);
        window.photoFixer._previewUrl = URL.createObjectURL(window.photoFixer._last);
        var img = document.getElementById("result-preview");
        if (img) img.src = window.photoFixer._previewUrl;
    },

    // Download the last encode.
    downloadLast: function (filename) {
        if (window.photoFixer._downloadUrl) URL.revokeObjectURL(window.photoFixer._downloadUrl);
        window.photoFixer._downloadUrl = URL.createObjectURL(window.photoFixer._last);
        var a = document.createElement("a");
        a.href = window.photoFixer._downloadUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
    }
};
