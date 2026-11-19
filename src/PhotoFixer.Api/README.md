# PhotoFixer self-host API

One of two PhotoFixer API branches:
- `photofixer-api-selfhost` (this branch) - keyless build each developer
  runs next to their own bot. Photos never leave the machine it runs on;
  nothing ever touches the owner's infrastructure.
- `photofixer-api-hosted` - adds developer API keys, quotas and rate
  limits for a shared endpoint the owner could operate later.

License: MIT (see LICENSE in this folder). The shared engine it wraps,
src/PhotoFixer.Core, is MIT-licensed too (LICENSE in that folder). The
website's own pages, design and code are NOT open-licensed. The engine is
used unchanged; this branch only adds src/PhotoFixer.Api/ and this CI
workflow.

## What it is

An HTTP wrapper around PhotoFixer.Core (the same tested C# engine that
powers https://photofixer.denelli.in). A bot or app sends a photo and
target size/KB and gets the processed photo back. Run one container or
process next to your bot and call it over localhost - works with any bot
language (Python, Node, Go, ...).

## Resource bounds

25 MB upload cap; input images over 50 MP or 16384 px per side are
refused before pixel allocation (decompression-bomb guard); 4 concurrent
processing slots with an honest 503 busy answer; 26 MB request body
ceiling. No photo bytes are ever written to disk or logged.

## Formats

v1 decodes JPG, PNG, WebP, GIF, BMP. HEIC and AVIF are answered with an
honest 422 (the website handles them through the browser's own codecs).
HEIC roadmap: Magick.NET (Apache-2.0) with native libheif.

## Endpoints

- `POST /v1/process` - multipart/form-data:
  - `file` (required) - the photo, max 25 MB
  - `targetWidth`, `targetHeight` (px, 16-10000, optional)
  - `maxKb` (5-20480, optional)
  - `mode` = `fit-inside` (default) | `fill-crop` | `stretch`
  - `format` = `auto` (default) | `jpeg` | `png`
  - `preset` (optional key from /v1/presets; fills exact dimensions and
    switches to fill-crop like the website; explicit fields override it)
  - Response 200: the processed image bytes (image/jpeg or image/png),
    with metadata headers X-PhotoFixer-Width / -Height / -Kb /
    -Jpeg-Quality / -Notes / -Warnings.
  - Errors: JSON `{"error": "..."}` (400 invalid request, 415 unknown
    format, 422 undecodable/too large/HEIC/AVIF, 503 busy).
- `GET /v1/presets` - named presets with the website's disclosure notes.
- `GET /healthz` - `ok`.

Example (Telegram/WhatsApp bot host):
`curl -F file=@photo.jpg -F preset=us-visa-online http://localhost:8080/v1/process -o fixed.jpg`

## Run

- Local: `dotnet run --project src/PhotoFixer.Api`
- Docker: `docker build -f src/PhotoFixer.Api/Dockerfile -t photofixer-api .`
  then `docker run -p 8080:8080 photofixer-api`.
  (Dockerfile statically reviewed; container build not yet run - no
  container runtime was available where this was developed.)

## Privacy

Everything happens in memory per request. No photo is written to disk,
stored, or logged. Nothing leaves the machine it runs on.
