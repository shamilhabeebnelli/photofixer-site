// PhotoFixer browser codec: pixel work only. All decisions are made in C#.
// Decoding: the browser's own maintained codecs (createImageBitmap + canvas)
// for every format the browser knows. HEIC/HEIF (iPhone photos), which
// Chrome/Firefox cannot decode natively, falls back to a vendored copy of
// libheif-js (LGPL-3.0, see wwwroot/vendor/LIBHEIF-LICENSE.txt) that loads
// lazily and only ever runs locally in this tab.
window.denelliPhotoFixer = {
    _bitmap: null,   // decoded source image (decoded once at upload)
    _canvas: null,   // OffscreenCanvas holding the crop+resize result
    _last: null,     // Blob of the most recent encode
    _previewUrl: null,
    _downloadUrl: null,
    _libheifPromise: null,

    // Identify the real format from magic bytes; never trusts the OS MIME hint.
    sniff: function (bytes) {
        var b = bytes;
        if (b.length > 3 && b[0] === 0xFF && b[1] === 0xD8 && b[2] === 0xFF) return "image/jpeg";
        if (b.length > 8 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4E && b[3] === 0x47) return "image/png";
        if (b.length > 6 && b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46) return "image/gif";
        if (b.length > 2 && b[0] === 0x42 && b[1] === 0x4D) return "image/bmp";
        if (b.length > 12 && b[0] === 0x52 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x46 &&
            b[8] === 0x57 && b[9] === 0x45 && b[10] === 0x42 && b[11] === 0x50) return "image/webp";
        if (b.length > 12 && b[4] === 0x66 && b[5] === 0x74 && b[6] === 0x79 && b[7] === 0x70) {
            var brand = String.fromCharCode(b[8], b[9], b[10], b[11]);
            if (brand === "avif" || brand === "avis") return "image/avif";
            // heic/heix/hevc/hevx/heim/heis/hevm/hevs/mif1/msf1 -> HEIF container
            if (brand === "mif1" || brand === "msf1" || brand.indexOf("he") === 0) return "image/heic";
        }
        return null;
    },

    // Load the vendored HEIC decoder on first use only.
    // libheif-bundle.js exposes an Emscripten factory; calling it returns the module.
    _ensureLibheif: function () {
        if (!window.denelliPhotoFixer._libheifPromise) {
            window.denelliPhotoFixer._libheifPromise = new Promise(function (resolve, reject) {
                var s = document.createElement("script");
                s.src = "vendor/libheif-bundle.js";
                s.onload = function () {
                    var mod = window.libheif();
                    Promise.resolve(mod).then(resolve, reject);
                };
                s.onerror = function () { reject(new Error("HEIC decoder failed to load")); };
                document.head.appendChild(s);
            });
        }
        return window.denelliPhotoFixer._libheifPromise;
    },

    // HEIC -> ImageBitmap via libheif (RGBA pixels) -> OffscreenCanvas.
    _decodeHeic: async function (bytes) {
        var libheif = await window.denelliPhotoFixer._ensureLibheif();
        var decoder = new libheif.HeifDecoder();
        var images = decoder.decode(bytes);
        if (!images || !images.length) throw new Error("No image found in HEIC file");
        var image = images[0];
        var w = image.get_width();
        var h = image.get_height();
        var canvas = new OffscreenCanvas(w, h);
        var ctx = canvas.getContext("2d");
        var imageData = ctx.createImageData(w, h);
        await new Promise(function (resolve, reject) {
            image.display(imageData, function (displayData) {
                if (!displayData) reject(new Error("HEIC pixel decode failed"));
                else resolve();
            });
        });
        ctx.putImageData(imageData, 0, 0);
        return await createImageBitmap(canvas);
    },

    _decode: async function (bytes) {
        var type = window.denelliPhotoFixer.sniff(bytes);
        if (!type) throw new Error("unrecognized format");
        if (type === "image/heic") {
            try {
                // Safari 17+ decodes HEIC natively; prefer the platform codec.
                var nativeBmp = await createImageBitmap(new Blob([bytes]));
                return { bitmap: nativeBmp, type: type };
            } catch (e) {
                var heicBmp = await window.denelliPhotoFixer._decodeHeic(bytes);
                return { bitmap: heicBmp, type: type };
            }
        }
        var bmp = await createImageBitmap(new Blob([bytes], { type: type }));
        return { bitmap: bmp, type: type };
    },

    // Decode once to read dimensions/format. Returns {width, height, type}.
    probe: async function (bytes, contentType) {
        var d = await window.denelliPhotoFixer._decode(bytes);
        var res = { width: d.bitmap.width, height: d.bitmap.height, type: d.type };
        d.bitmap.close();
        return res;
    },

    // Keep the decoded source for repeated renders without re-uploading bytes.
    setSource: async function (bytes, contentType) {
        if (window.denelliPhotoFixer._bitmap) window.denelliPhotoFixer._bitmap.close();
        var d = await window.denelliPhotoFixer._decode(bytes);
        window.denelliPhotoFixer._bitmap = d.bitmap;
    },

    // Crop (cx,cy,cw,ch in source pixels) + resize to (ow,oh) onto the working canvas.
    render: function (cx, cy, cw, ch, ow, oh) {
        var bmp = window.denelliPhotoFixer._bitmap;
        var canvas = new OffscreenCanvas(ow, oh);
        var ctx = canvas.getContext("2d");
        ctx.fillStyle = "#FFFFFF"; // JPEG has no alpha; composite on white, never black
        ctx.fillRect(0, 0, ow, oh);
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = "high";
        ctx.drawImage(bmp, cx, cy, cw, ch, 0, 0, ow, oh);
        window.denelliPhotoFixer._canvas = canvas;
    },

    // Encode the working canvas as JPEG; quality is 0.05-0.95. Returns byte size.
    encodeJpeg: async function (quality) {
        var blob = await window.denelliPhotoFixer._canvas.convertToBlob({ type: "image/jpeg", quality: quality });
        window.denelliPhotoFixer._last = blob;
        return blob.size;
    },

    // Encode the working canvas as PNG. Returns byte size.
    encodePng: async function () {
        var blob = await window.denelliPhotoFixer._canvas.convertToBlob({ type: "image/png" });
        window.denelliPhotoFixer._last = blob;
        return blob.size;
    },

    // Bytes of the most recent encode (for the C# result record).
    lastBytes: async function () {
        return new Uint8Array(await window.denelliPhotoFixer._last.arrayBuffer());
    },

    // Show the last encode in the preview <img>.
    setPreviewFromLast: function () {
        if (window.denelliPhotoFixer._previewUrl) URL.revokeObjectURL(window.denelliPhotoFixer._previewUrl);
        window.denelliPhotoFixer._previewUrl = URL.createObjectURL(window.denelliPhotoFixer._last);
        var img = document.getElementById("result-preview");
        if (img) img.src = window.denelliPhotoFixer._previewUrl;
    },

    // Download the last encode.
    downloadLast: function (filename) {
        if (window.denelliPhotoFixer._downloadUrl) URL.revokeObjectURL(window.denelliPhotoFixer._downloadUrl);
        window.denelliPhotoFixer._downloadUrl = URL.createObjectURL(window.denelliPhotoFixer._last);
        var a = document.createElement("a");
        a.href = window.denelliPhotoFixer._downloadUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
    }
};
