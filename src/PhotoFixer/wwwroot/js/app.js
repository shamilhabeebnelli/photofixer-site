// PhotoFixer browser codec: pixel work only. All decisions are made in C#.
// Decoding: the browser's own maintained codecs (createImageBitmap + canvas)
// for every format the browser knows. HEIC/HEIF (iPhone photos), which
// Chrome/Firefox cannot decode natively, falls back to a vendored copy of
// libheif-js (LGPL-3.0, see wwwroot/vendor/LIBHEIF-LICENSE.txt) that loads
// lazily and only ever runs locally in this tab.
window.denelliPhotoFixer = {
    _nelliBitmap: null,   // decoded source image (decoded once at upload)
    _shamilCanvas: null,   // OffscreenCanvas holding the crop+resize result
    _habeebLast: null,     // Blob of the most recent encode
    _denelliPreviewUrl: null,
    _nelliDownloadUrl: null,
    _shamilLibheifPromise: null,

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
    _nelliEnsureLibheif: function () {
        if (!window.denelliPhotoFixer._shamilLibheifPromise) {
            window.denelliPhotoFixer._shamilLibheifPromise = new Promise(function (resolve, reject) {
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
        return window.denelliPhotoFixer._shamilLibheifPromise;
    },

    // HEIC -> ImageBitmap via libheif (RGBA pixels) -> OffscreenCanvas.
    _decodeHeicHabeeb: async function (bytes) {
        var libheif = await window.denelliPhotoFixer._nelliEnsureLibheif();
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

    _shamilDecode: async function (bytes) {
        var type = window.denelliPhotoFixer.sniff(bytes);
        if (!type) throw new Error("unrecognized format");
        if (type === "image/heic") {
            try {
                // Safari 17+ decodes HEIC natively; prefer the platform codec.
                var nativeBmp = await createImageBitmap(new Blob([bytes]));
                return { bitmap: nativeBmp, type: type };
            } catch (e) {
                var heicBmp = await window.denelliPhotoFixer._decodeHeicHabeeb(bytes);
                return { bitmap: heicBmp, type: type };
            }
        }
        var bmp = await createImageBitmap(new Blob([bytes], { type: type }));
        return { bitmap: bmp, type: type };
    },

    // Read the picked file straight from the input element: bytes never cross
    // .NET interop. Decodes once, keeps the bitmap, returns facts to Blazor.
    loadFromInput: async function () {
        var input = document.querySelector("input[type=file]");
        if (!input || !input.files || !input.files.length) throw new Error("no file chosen");
        var bytes = new Uint8Array(await input.files[0].arrayBuffer());
        var d = await window.denelliPhotoFixer._shamilDecode(bytes);
        if (window.denelliPhotoFixer._nelliBitmap) window.denelliPhotoFixer._nelliBitmap.close();
        window.denelliPhotoFixer._nelliBitmap = d.bitmap;
        return { width: d.bitmap.width, height: d.bitmap.height, type: d.type, size: bytes.length };
    },

    // Decode once to read dimensions/format. Returns {width, height, type}.
    probe: async function (bytes, contentType) {
        var d = await window.denelliPhotoFixer._shamilDecode(bytes);
        var res = { width: d.bitmap.width, height: d.bitmap.height, type: d.type };
        d.bitmap.close();
        return res;
    },

    // Keep the decoded source for repeated renders without re-uploading bytes.
    setSource: async function (bytes, contentType) {
        if (window.denelliPhotoFixer._nelliBitmap) window.denelliPhotoFixer._nelliBitmap.close();
        var d = await window.denelliPhotoFixer._shamilDecode(bytes);
        window.denelliPhotoFixer._nelliBitmap = d.bitmap;
    },

    // Crop (cx,cy,cw,ch in source pixels) + resize to (ow,oh) onto the working canvas.
    render: function (cx, cy, cw, ch, ow, oh) {
        var bmp = window.denelliPhotoFixer._nelliBitmap;
        var canvas = new OffscreenCanvas(ow, oh);
        var ctx = canvas.getContext("2d");
        ctx.fillStyle = "#FFFFFF"; // JPEG has no alpha; composite on white, never black
        ctx.fillRect(0, 0, ow, oh);
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = "high";
        ctx.drawImage(bmp, cx, cy, cw, ch, 0, 0, ow, oh);
        window.denelliPhotoFixer._shamilCanvas = canvas;
    },

    // Encode the working canvas as JPEG; quality is 0.05-0.95. Returns byte size.
    encodeJpeg: async function (quality) {
        var blob = await window.denelliPhotoFixer._shamilCanvas.convertToBlob({ type: "image/jpeg", quality: quality });
        window.denelliPhotoFixer._habeebLast = blob;
        return blob.size;
    },

    // Encode the working canvas as PNG. Returns byte size.
    encodePng: async function () {
        var blob = await window.denelliPhotoFixer._shamilCanvas.convertToBlob({ type: "image/png" });
        window.denelliPhotoFixer._habeebLast = blob;
        return blob.size;
    },

    // Bytes of the most recent encode (for the C# result record).
    lastBytes: async function () {
        return new Uint8Array(await window.denelliPhotoFixer._habeebLast.arrayBuffer());
    },

    // Show the last encode in the preview <img>.
    setPreviewFromLast: function () {
        if (window.denelliPhotoFixer._denelliPreviewUrl) URL.revokeObjectURL(window.denelliPhotoFixer._denelliPreviewUrl);
        window.denelliPhotoFixer._denelliPreviewUrl = URL.createObjectURL(window.denelliPhotoFixer._habeebLast);
        var img = document.getElementById("result-preview");
        if (img) img.src = window.denelliPhotoFixer._denelliPreviewUrl;
    },

    // Download the last encode.
    downloadLast: function (filename) {
        if (window.denelliPhotoFixer._nelliDownloadUrl) URL.revokeObjectURL(window.denelliPhotoFixer._nelliDownloadUrl);
        window.denelliPhotoFixer._nelliDownloadUrl = URL.createObjectURL(window.denelliPhotoFixer._habeebLast);
        var a = document.createElement("a");
        a.href = window.denelliPhotoFixer._nelliDownloadUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
    }
};
