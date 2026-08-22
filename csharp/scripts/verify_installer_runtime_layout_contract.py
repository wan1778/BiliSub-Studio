from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")


inno = read("csharp/installer/BiliSubStudio.iss")
installer = read("csharp/scripts/build_windows_installer.ps1")
paths = read("csharp/src/BiliSubStudio.Core/Configuration/AppPaths.cs")
updater = read("csharp/src/BiliSubStudio.Core/Maintenance/UpdateService.cs")

require('DestDir: "{app}\\Runtime"' in inno, "verified publish must install under Runtime, not the user-visible root")
require('UninstallDisplayIcon={app}\\Runtime\\BiliSubStudio.exe' in inno, "uninstall icon must point at nested runtime")
require('Filename: "{app}\\Runtime\\BiliSubStudio.exe"' in inno, "Start/Desktop shortcuts must launch the nested runtime")
require('Filename: "{app}\\Runtime\\BiliSubStudio.exe"; Description: "Mở BiliSub Studio"' in inno, "post-install launch must use nested runtime")
for protected in ("Data", "Tools", "Temp", "Cache", "Downloads"):
    require(f'Name: "{{app}}\\{protected}"; Flags: uninsneveruninstall' in inno, f"protected root lost: {protected}")
require("CleanupLegacyFlatRuntime" in inno and "SHA256SUMS.txt" in inno, "legacy flat runtime must migrate by checksum ownership")
require("Unknown or user-owned root files are left untouched" in inno, "migration must document unknown-file preservation")

require('InstalledRuntimeDirectoryName = "Runtime"' in paths, "AppPaths must own the installed runtime directory name")
require("Path.GetFileName(executableDirectory), InstalledRuntimeDirectoryName" in paths, "AppPaths must detect execution from Runtime")
require("Directory.GetParent(executableDirectory)?.FullName" in paths, "installed Runtime must resolve Data/Tools/Cache to the parent install root")

require('"--apply-portable-update", update.PayloadDirectory, Path.GetFullPath(AppContext.BaseDirectory)' in updater, "updater must replace the runtime directory, not the user-data root")
require("Path.GetFileName(target), AppPaths.InstalledRuntimeDirectoryName" in updater, "updater rollback must recognize nested Runtime")
require('Path.Combine(targetParent, "Temp", "Update"' in updater, "nested-runtime rollback must live in the parent protected Temp root")

require('$installedRuntime = Join-Path $installRoot "Runtime"' in installer, "installer smoke must validate Runtime directory")
require('$installedExe = Join-Path $installedRuntime "BiliSubStudio.exe"' in installer, "installer smoke must launch nested executable")
require('Test-Path (Join-Path $installRoot "BiliSubStudio.exe")' in installer, "installer smoke must reject leftover flat executable")
require("legacy_flat_runtime_migration_smoke = $legacyFlatMigrationSmoke" in installer, "installer evidence must record legacy migration smoke")
require("user-file-not-owned-by-runtime.keep" in installer and "DO NOT DELETE" in installer, "migration smoke must prove unknown root files survive")
require("Assert-ChecksumFile $installedRuntime $installedSums" in installer, "installed Runtime must match the verified publish checksum inventory")

print("PASS: tidy Runtime installer / legacy-flat migration / protected-data / updater-target contracts")
