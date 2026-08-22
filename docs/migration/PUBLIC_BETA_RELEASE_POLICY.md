# BiliSub Studio public beta release policy

Effective for `4.0.0-beta.12-csharp-p5` and subsequent beta builds.

- Public beta releases may be published after the Windows CI/build/installer gates pass even when real-machine field QA is still ongoing.
- Stable `4.0.0` remains a separate quality gate and must not be implied by a beta release.
- Real-machine defects found after public beta publication are fixed in subsequent beta builds and distributed through `update/beta.json`.
- Each beta release must publish an exact installer, portable WinUI runtime ZIP for the updater, exact source archive, and SHA-256 checksums.
- The user-visible install root must expose `BiliSubStudio.exe`; the full runtime remains under `Runtime\`, and uninstall-owned files remain under `Uninstall\`.
- Protected data roots remain `Data`, `Tools`, `Temp`, `Cache`, and `Downloads`.
- Long-media CDN failover regressions remain mandatory build gates.
