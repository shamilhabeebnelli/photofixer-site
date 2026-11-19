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
    _pdfjsPromise: null,      // lazy pdf.js (page rendering)
    _pdflibPromise: null,     // lazy pdf-lib (PDF rebuilding)
    _pdfDoc: null,            // open pdf.js document
    _pdfBaseCanvases: null,   // one canvas per page at base scale (rendered once)
    _pdfPageSizes: null,      // page sizes in PDF points
    _pdfScaleFrac: 1,         // current downscale fraction set by the engine
    _pdfPreview: null,        // first-page JPEG blob, only for the preview <img>
    PDF_BASE_SCALE: 1.5,      // 108 DPI rasterisation baseline
    PDF_MAX_PAGES: 40,

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
        if (b.length > 4 && b[0] === 0x25 && b[1] === 0x50 && b[2] === 0x44 && b[3] === 0x46) return "application/pdf";
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
        if (window.denelliPhotoFixer.sniff(bytes) === "application/pdf") {
            return await window.denelliPhotoFixer._pdfLoad(bytes);
        }
        var d = await window.denelliPhotoFixer._shamilDecode(bytes);
        if (window.denelliPhotoFixer._nelliBitmap) window.denelliPhotoFixer._nelliBitmap.close();
        window.denelliPhotoFixer._nelliBitmap = d.bitmap;
        window.denelliPhotoFixer._pdfClear();
        return { width: d.bitmap.width, height: d.bitmap.height, type: d.type, size: bytes.length };
    },

    // --- PDF support: rasterise pages with pdf.js, rebuild with pdf-lib ---
    // pdf.js (Apache-2.0, wwwroot/vendor/pdf.min.js + pdf.worker.min.js) and
    // pdf-lib (MIT, wwwroot/vendor/pdf-lib.min.js) load lazily on first PDF,
    // exactly like the HEIC decoder. Everything runs locally in this tab.

    _pdfLoadScript: function (src) {
        return new Promise(function (resolve, reject) {
            var s = document.createElement("script");
            s.src = src;
            s.onload = resolve;
            s.onerror = function () { reject(new Error(src + " failed to load")); };
            document.head.appendChild(s);
        });
    },

    _pdfEnsurePdfjs: function () {
        if (!window.denelliPhotoFixer._pdfjsPromise) {
            window.denelliPhotoFixer._pdfjsPromise = window.denelliPhotoFixer
                ._pdfLoadScript("vendor/pdf.min.js")
                .then(function () {
                    window.pdfjsLib.GlobalWorkerOptions.workerSrc = "vendor/pdf.worker.min.js";
                    return window.pdfjsLib;
                });
        }
        return window.denelliPhotoFixer._pdfjsPromise;
    },

    _pdfEnsurePdflib: function () {
        if (!window.denelliPhotoFixer._pdflibPromise) {
            window.denelliPhotoFixer._pdflibPromise = window.denelliPhotoFixer
                ._pdfLoadScript("vendor/pdf-lib.min.js")
                .then(function () { return window.PDFLib; });
        }
        return window.denelliPhotoFixer._pdflibPromise;
    },

    _pdfClear: function () {
        window.denelliPhotoFixer._pdfDoc = null;
        window.denelliPhotoFixer._pdfBaseCanvases = null;
        window.denelliPhotoFixer._pdfPageSizes = null;
        window.denelliPhotoFixer._pdfPreview = null;
        window.denelliPhotoFixer._pdfScaleFrac = 1;
    },

    // Open the PDF and rasterise every page once at the base scale.
    // Returns facts for Blazor: width/height are the first page's raster
    // pixels at base scale - the engine treats them like a photo's pixels.
    _pdfLoad: async function (bytes) {
        var pf = window.denelliPhotoFixer;
        var pdfjsLib = await pf._pdfEnsurePdfjs();
        await pf._pdfEnsurePdflib();
        var origSize = bytes.length;
        // pdf.js detaches the buffer it is given; keep a copy for the
        // "already under the limit" path, which returns the original file.
        var keep = bytes.slice();
        var doc = await pdfjsLib.getDocument({ data: bytes }).promise;
        if (doc.numPages > pf.PDF_MAX_PAGES) {
            throw new Error("This PDF has " + doc.numPages + " pages; this tool handles up to " + pf.PDF_MAX_PAGES + " pages.");
        }
        var canvases = [];
        var sizes = [];
        for (var p = 1; p <= doc.numPages; p++) {
            var page = await doc.getPage(p);
            var vp1 = page.getViewport({ scale: 1 });
            sizes.push([vp1.width, vp1.height]); // points
            var vp = page.getViewport({ scale: pf.PDF_BASE_SCALE });
            var canvas = new OffscreenCanvas(Math.ceil(vp.width), Math.ceil(vp.height));
            var ctx = canvas.getContext("2d");
            ctx.fillStyle = "#FFFFFF"; // JPEG has no alpha; composite on white
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            await page.render({ canvasContext: ctx, viewport: vp }).promise;
            canvases.push(canvas);
        }
        pf._pdfClear();
        pf._pdfDoc = doc;
        pf._pdfBaseCanvases = canvases;
        pf._pdfPageSizes = sizes;
        pf._pdfScaleFrac = 1;
        // Until the first build runs, "last" is the original file itself and
        // the preview is a JPEG of the first page at base scale.
        pf._habeebLast = new Blob([keep], { type: "application/pdf" });
        pf._pdfPreview = await canvases[0].convertToBlob({ type: "image/jpeg", quality: 0.85 });
        return {
            width: canvases[0].width,
            height: canvases[0].height,
            type: "application/pdf",
            size: origSize,
            pages: doc.numPages
        };
    },

    // The engine sets the downscale fraction (1 = base scale); building
    // happens in pdfBuild so every encode measures a real candidate.
    pdfRender: function (frac) {
        window.denelliPhotoFixer._pdfScaleFrac = frac;
    },

    // Downscale the rendered pages to the current fraction, JPEG-encode each
    // and rebuild a one-image-per-page PDF at the original page sizes.
    // Keeps the PDF as "last" and the first page's JPEG as the preview.
    pdfBuild: async function (quality) {
        var pf = window.denelliPhotoFixer;
        if (!pf._pdfBaseCanvases) throw new Error("no PDF loaded");
        var PDFLib = await pf._pdfEnsurePdflib();
        var out = await PDFLib.PDFDocument.create();
        var frac = pf._pdfScaleFrac;
        for (var i = 0; i < pf._pdfBaseCanvases.length; i++) {
            var base = pf._pdfBaseCanvases[i];
            var w = Math.max(16, Math.round(base.width * frac));
            var h = Math.max(16, Math.round(base.height * frac));
            var canvas = new OffscreenCanvas(w, h);
            var ctx = canvas.getContext("2d");
            ctx.fillStyle = "#FFFFFF";
            ctx.fillRect(0, 0, w, h);
            ctx.imageSmoothingEnabled = true;
            ctx.imageSmoothingQuality = "high";
            ctx.drawImage(base, 0, 0, w, h);
            var blob = await canvas.convertToBlob({ type: "image/jpeg", quality: quality });
            if (i === 0) pf._pdfPreview = blob;
            var img = await out.embedJpg(await blob.arrayBuffer());
            var pts = pf._pdfPageSizes[i];
            var pdfPage = out.addPage([pts[0], pts[1]]);
            pdfPage.drawImage(img, { x: 0, y: 0, width: pts[0], height: pts[1] });
        }
        var pdfBytes = await out.save();
        pf._habeebLast = new Blob([pdfBytes], { type: "application/pdf" });
        return pf._habeebLast.size;
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

    // Show the last encode in the preview <img>. For PDF results the last
    // blob is the PDF itself, which an <img> cannot show - show the stored
    // first-page JPEG instead.
    setPreviewFromLast: function () {
        var pf = window.denelliPhotoFixer;
        var blob = pf._pdfPreview || pf._habeebLast;
        if (pf._denelliPreviewUrl) URL.revokeObjectURL(pf._denelliPreviewUrl);
        pf._denelliPreviewUrl = URL.createObjectURL(blob);
        var img = document.getElementById("result-preview");
        if (img) img.src = pf._denelliPreviewUrl;
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
