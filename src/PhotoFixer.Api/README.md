# PhotoFixer API - hosted/shared variant (code-ready, NOT deployed)

One of two PhotoFixer API branches:
- `photofixer-api-selfhost` - keyless build each developer runs next to
  their own bot (no photos ever touch the owner's infrastructure).
- `photofixer-api-hosted` (this branch) - adds developer API keys, quotas
  and rate limits for a shared endpoint the owner could operate later.

License: MIT (see LICENSE in this folder). The shared engine it wraps,
src/PhotoFixer.Core, is MIT-licensed too (LICENSE in that folder). The
website's own pages, design and code are NOT open-licensed. The engine is
used unchanged; this branch only adds src/PhotoFixer.Api/ and this CI
workflow.

## What it is

An HTTP wrapper around PhotoFixer.Core (the same tested C# engine that
powers https://photofixer.denelli.in). A bot or app sends a photo and
target size/KB and gets the processed photo back.

## Hosted variant controls

Enabled with configuration, no code change:

- `PhotoFixer__RequireApiKey=true` - require an `X-Api-Key` header on
  /v1/process (401 missing, 403 invalid)
- `PhotoFixer__Keys__0="dev-name:<sha256-hex-of-key>"` - one entry per
  developer; only the SHA-256 hash is stored. Operators generate keys
  with e.g. `openssl rand -hex 24` and hand them to developers directly.
- `PhotoFixer__DailyQuota=200` - requests per key per UTC day (429 over)
- `PhotoFixer__PerMinute=30` - burst limit per key (429 over)

Resource bounds (always on, both branches): 25 MB upload cap, input
images over 50 MP or 16384 px per side refused before pixel allocation
(decompression-bomb guard), 4 concurrent processing slots (503 busy
answer), 26 MB Kestrel body ceiling. No photo bytes are ever written to
disk or logged; request logs contain paths only.

NOT yet done before any real public hosted deployment:
- Quotas/rate limits are in-memory and reset on restart - a public
  deployment needs a persistent store.
- Hosting choice and cost, TLS/domain, privacy notice and abuse
  monitoring for strangers' photos transiting the server.

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
  - Errors: JSON `{"error": "..."}` (400 invalid request, 401/403 key
    problems, 415 unknown format, 422 undecodable/too large/HEIC/AVIF,
    429 quota, 503 busy).
- `GET /v1/presets` - named presets with the website's disclosure notes.
- `GET /healthz` - `ok`.

Example (Telegram bot host):
`curl -H "X-Api-Key: <key>" -F file=@photo.jpg -F preset=us-visa-online https://<host>/v1/process -o fixed.jpg`

## Run

- Local: `dotnet run --project src/PhotoFixer.Api`
- Docker: `docker build -f src/PhotoFixer.Api/Dockerfile -t photofixer-api .`
  then `docker run -p 8080:8080 photofixer-api`.
  (Dockerfile statically reviewed; container build not yet run - no
  container runtime was available where this was developed.)

## Privacy

Everything happens in memory per request. No photo is written to disk,
stored, or logged.
