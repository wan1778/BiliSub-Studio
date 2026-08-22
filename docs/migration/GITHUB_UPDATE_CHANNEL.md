# GitHub update channel contract

BiliSub Studio no longer uses Google Drive for application updates.

## Source of truth

- Stable manifest: `update/stable.json` on the repository `main` branch.
- Beta manifest: `update/beta.json` on the repository `main` branch.
- Runtime payload: a GitHub Release asset in `wan1778/BiliSub-Studio`.
- The application reads manifests through `raw.githubusercontent.com` and accepts payload URLs only under `https://github.com/wan1778/BiliSub-Studio/releases/download/...`.

No Drive file ID, Drive download URL, external mirror, arbitrary CDN URL or unverified direct binary is a valid production update source.

## Manifest schema

```json
{
  "version": "4.0.0-beta.13",
  "channel_ready": true,
  "download_url": "https://github.com/wan1778/BiliSub-Studio/releases/download/v4.0.0-beta.13/BiliSubStudio_v4.0.0-beta.13_winui3-portable.zip",
  "sha256": "<64 lowercase hex characters>",
  "size": 123456789,
  "payload_kind": "winui3-portable-zip",
  "notes": ["..."]
}
```

`channel_ready=false` is the safe unpublished state. In that state `download_url`, `sha256` and `size` may remain empty/zero and the app must not prepare an update.

## Promotion sequence

1. Complete source review, Windows CI and real-machine field QA for an exact Git commit and installer SHA-256.
2. Only after approval, create the GitHub Release and attach the exact WinUI portable update ZIP.
3. Calculate the final asset byte size and SHA-256 after upload/download verification.
4. Update the relevant `update/stable.json` or `update/beta.json` on `main` with the GitHub Release asset URL, exact size/hash and `channel_ready=true`.
5. Keep the other channel independent. Stable builds read only stable; prerelease builds read only beta.

## Runtime safety

The source migration changes only update discovery/hosting. Existing safety gates remain mandatory:

- `payload_kind=winui3-portable-zip`;
- exact size readback;
- SHA-256 readback;
- safe ZIP extraction and path traversal rejection;
- exactly one root `BiliSubStudio.exe` payload owner;
- PE x86-64 / PE32+ validation;
- protected `Data/Tools/Temp/Cache/Downloads` roots excluded from payload replacement;
- updater breakaway process;
- transactional runtime swap and rollback on failure.

During the current field-QA phase both manifests remain unpublished and no GitHub Release is created automatically.
