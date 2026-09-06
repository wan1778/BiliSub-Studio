# Timeline Editor V2 - Production Project Backup

Use this procedure before any migration experiment or write to a V2 persistence root.

## Safety rules

- Resolve the installed runtime from the Windows uninstall registry; do not assume a build-output directory is production.
- Confirm that `BiliSubStudio.exe` is not running.
- Copy the complete production `Data/Projects` tree, including sidecars and in-progress files.
- Write into a unique timestamped `.partial` directory outside the installation and repository.
- Record relative path, byte length and SHA-256 for every source file.
- Compare source and copied inventories, lengths and hashes before promoting the directory.
- Rename `.partial` only after verification succeeds, then mark backup files read-only.
- Never overwrite production during restore. Restore first into a separate test root and verify the manifest again.

## G0 backup evidence

- Production root: `E:\New folder\testrc\Data\Projects` (resolved from the registered install location).
- Backup root: `C:\Users\Man PC\Documents\BiliSub Studio Backups\Projects-G0-20260906T162516Z`.
- Baseline tag: `timeline-v2-g0-baseline-20260906`.
- Files: `7`.
- Bytes: `6,366,311`.
- Manifest SHA-256: `a983c21e4adb6646f31370761e91e3abf3cb5db887bba9ba74677406dfa69138`.
- Verification result: source/copy inventory, byte length and SHA-256 all matched.

The backup contains local project data and must not be committed to the repository.
