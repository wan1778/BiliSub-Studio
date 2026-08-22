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
launcher = read("csharp/src/BiliSubStudio.Launcher/Program.cs")
launcher_project = read("csharp/src/BiliSubStudio.Launcher/BiliSubStudio.Launcher.csproj")

require('DestDir: "{app}\\Runtime"' in inno, "verified publish must install under Runtime, not the user-visible root")
require('DestName: "BiliSubStudio.exe"' in inno and 'DestDir: "{app}"' in inno, "installer must expose one root BiliSubStudio.exe launcher")
require('UninstallDisplayIcon={app}\\BiliSubStudio.exe' in inno, "uninstall icon must use the root launcher")
require('UninstallFilesDir={app}\\Uninstall' in inno, "uninstall-owned files must be isolated from the user-visible root")
require('Filename: "{app}\\BiliSubStudio.exe"' in inno, "Start/Desktop shortcuts must launch the root executable")
require('Filename: "{app}\\BiliSubStudio.exe"; Description: "Mở BiliSub Studio"' in inno, "post-install launch must use the root executable")
for protected in ("Data", "Tools", "Temp", "Cache", "Downloads"):
    require(f'Name: "{{app}}\\{protected}"; Flags: uninsneveruninstall' in inno, f"protected root lost: {protected}")
require("CleanupLegacyFlatRuntime" in inno and "SHA256SUMS.txt" in inno, "legacy flat runtime must migrate by checksum ownership")
require("Unknown or user-owned root files are left untouched" in inno, "migration must document unknown-file preservation")
require("CleanupLegacyRootUninstaller" in inno, "legacy root uninstaller files must be tidied on upgrade")

require('Path.Combine(root, "Runtime")' in launcher and 'Path.Combine(runtimeDirectory, "BiliSubStudio.exe")' in launcher, "root launcher must target nested WinUI runtime")
require("startInfo.ArgumentList.Add(argument)" in launcher, "root launcher must forward command-line arguments")
require("UseShellExecute = true" in launcher and "WorkingDirectory = runtimeDirectory" in launcher, "root launcher must start the nested runtime with the correct working directory")
require("<OutputType>WinExe</OutputType>" in launcher_project, "root launcher must not open a console window")
require("<RuntimeIdentifier>win-x64</RuntimeIdentifier>" in launcher_project, "root launcher must target x64 Windows")

require('InstalledRuntimeDirectoryName = "Runtime"' in paths, "AppPaths must own the installed runtime directory name")
require("Path.GetFileName(executableDirectory), InstalledRuntimeDirectoryName" in paths, "AppPaths must detect execution from Runtime")
require("Directory.GetParent(executableDirectory)?.FullName" in paths, "installed Runtime must resolve Data/Tools/Cache to the parent install root")

require('"--apply-portable-update", update.PayloadDirectory, Path.GetFullPath(AppContext.BaseDirectory)' in updater, "updater must replace the runtime directory, not the user-data root")
require("Path.GetFileName(target), AppPaths.InstalledRuntimeDirectoryName" in updater, "updater rollback must recognize nested Runtime")
require('Path.Combine(targetParent, "Temp", "Update"' in updater, "nested-runtime rollback must live in the parent protected Temp root")

require('$rootLauncher = Join-Path $installRoot "BiliSubStudio.exe"' in installer, "installer smoke must validate root launcher")
require('$installedRuntime = Join-Path $installRoot "Runtime"' in installer, "installer smoke must validate Runtime directory")
require('$installedExe = Join-Path $installedRuntime "BiliSubStudio.exe"' in installer, "installer smoke must retain nested runtime executable")
require("root launcher smoke" in installer.lower() and "$rootLauncherSmoke = $true" in installer, "installer smoke must actually start the app through the root launcher")
require('Join-Path $installRoot "Uninstall"' in installer, "installer smoke must validate isolated uninstall files")
require("legacy_flat_runtime_migration_smoke = $legacyFlatMigrationSmoke" in installer, "installer evidence must record legacy migration smoke")
require("root_launcher_smoke = $rootLauncherSmoke" in installer, "installer evidence must record root-launcher smoke")
require("user-file-not-owned-by-runtime.keep" in installer and "DO NOT DELETE" in installer, "migration smoke must prove unknown root files survive")
require("Assert-ChecksumFile $installedRuntime $installedSums" in installer, "installed Runtime must match the verified publish checksum inventory")
require("-p:PublishAot=true" in installer, "packaging must build the root launcher as a small native-AOT executable")

print("PASS: root launcher + tidy Runtime/Uninstall installer / legacy migration / protected-data / updater-target contracts")
