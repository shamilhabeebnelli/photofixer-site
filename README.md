# PhotoFixer

Copyright (c) 2026 Nelli. All rights reserved. This repository is public for
transparency and hosting; it is not open source, and no license to copy or
redistribute this code is granted.

Resize, crop and compress a photo to the exact pixel dimensions and
KB limit an online form asks for. Runs entirely in the browser (C# / Blazor
WebAssembly): the photo never leaves the device. No account, no server, no
tracking, and camera/location metadata (EXIF) is stripped from the result.

## What it does

- Upload JPG, PNG, WebP, GIF, BMP, AVIF or HEIC/HEIF (iPhone photos), up to 30 MB.
  HEIC is decoded locally in the tab (see Third-party components); output is
  JPEG or PNG.
- Enter any of: target width (px), target height (px), max size (KB).
- Three resize modes:
  - **Fit inside** - keeps the whole photo, shrinks proportionally.
  - **Fill & crop center** - exact width x height, center-cropped.
  - **Stretch to exact size** - exact size, ignores proportions (warns).
- Picks the highest JPEG quality that fits the KB limit; scales down further
  only if quality alone cannot reach it.
- Honest feedback: warns about low quality, upscaling, PNG vs JPEG size
  trade-offs, and when a target cannot be reached.

## What it does not do

- It cannot guarantee a destination form will accept the result. Every site
  applies its own checks.
- No official-form presets. Type the numbers the form itself states.
- Very small KB targets always cost visible quality.

## Tech

- .NET 10, Blazor WebAssembly (static site after publish).
- No third-party .NET packages at all. Pixel decoding/encoding uses the
  browser's own maintained codecs (createImageBitmap + canvas) through a
  small JS bridge; every decision - validation, crop geometry, the JPEG
  quality binary search, when shrinking is allowed, when to fail - is C#
  (src/PhotoFixer.Core).
- Canvas re-encoding also strips EXIF metadata automatically.

## Third-party components

- **libheif-js 1.23.5** (an Emscripten build of
  [libheif](https://github.com/strukturag/libheif), by strukturag AG and
  contributors; JS wrapper by catdad-experiments), licensed under the
  **GNU Lesser General Public License v3.0**. Used only to decode HEIC/HEIF
  photos in browsers without native HEIC support (e.g. Chrome, Firefox);
  it loads lazily from `wwwroot/vendor/` on first HEIC upload and runs
  entirely on the device. License texts ship with the app
  (`wwwroot/vendor/LIBHEIF-LICENSE.txt`,
  `wwwroot/vendor/LIBHEIF-JS-LICENSE.txt`). Source:
  https://github.com/catdad-experiments/libheif-js and
  https://github.com/strukturag/libheif. As an LGPL component kept as a
  separate replaceable module, it may be replaced by any compatible build;
  the LGPL applies to that module only, not to PhotoFixer's own code, which
  remains all-rights-reserved as stated above.

## Run locally

Needs the .NET 10 SDK (free from https://dot.net/download):

    dotnet run --project src/PhotoFixer
    # open http://localhost:5080

## Publish (free hosting)

    dotnet publish src/PhotoFixer -c Release

Output lands in `src/PhotoFixer/bin/Release/net10.0/publish/wwwroot` as plain
static files. Any static host works, including free ones (GitHub Pages,
Netlify, Cloudflare Pages). For GitHub Pages under a repo subpath, change
`<base href="/">` in `wwwroot/index.html` to `/<repo-name>/` before publish.

## Hard guarantees

- Fill & crop center and Stretch keep the exact pixel dimensions you enter,
  always. If the KB target cannot be reached at that exact size, PhotoFixer
  fails with a clear message naming the smallest achievable KB and asks you
  to relax the KB or the dimensions. It never silently shrinks an exact
  size.
- Fit inside treats your numbers as maximums. If quality alone cannot reach
  the KB target it scales down below those maximums, and the result always
  says so.

## Tests

- `src/PhotoFixer.Verify` - console harness for the C# engine (25 checks):
  geometry, quality search, exact-dims never shrink, fit-inside shrink
  disclosure, PNG honesty, validation, warnings. Uses a fake codec; the real
  codecs are covered by the browser test. `dotnet run --project src/PhotoFixer.Verify`
- The published site was verified end-to-end in headless Chrome: upload,
  process, preview, download (file re-verified as exact-dims JPEG with no
  EXIF), zero network requests during processing, and the impossible-target
  failure path.

## Known notes

- First page load downloads the .NET WebAssembly runtime (a few MB once;
  cached afterwards).
- Needs a modern browser (OffscreenCanvas + WebAssembly): current Chrome,
  Edge, Firefox, Safari 16.4+.
