from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")


inno = read("csharp/installer/BiliSubStudio.iss")
installer = read("csharp/scripts/build_windows_installer.ps1")
package = read("csharp/scripts/package_windows_candidate.ps1")
workflow = read(".github/workflows/csharp-p5-windows-x64-installer.yml")
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
require("GITHUB_ACTIONS" not in installer, "local packaging must not skip the installer smoke outside CI")
require("$workTempRoot" in installer and "$smokeContainer" in installer,
        "installer smoke must use an isolated temporary root")
require("try {" in installer and "finally {" in installer
        and "Remove-Item $smokeContainer -Recurse -Force" in installer,
        "installer smoke and launcher staging must clean up through finally")
require('Join-Path $smokeContainer "installed-startup-smoke.txt"' in installer
        and '(Get-Content $smokeSentinel -Raw).Trim() -ne "PASS"' in installer,
        "installer startup smoke needs a per-run sentinel with PASS content")
require("public_beta_ready = $publicBetaReady" in installer,
        "installer evidence must derive readiness from completed smoke flags")
for field in ("public_beta_ready", "installer_install_smoke", "root_launcher_smoke",
              "legacy_flat_runtime_migration_smoke"):
    require(f"Assert-TrueBoolean $installerStatus.{field}" in package,
            f"candidate package fail-closed check missing: {field}")
require("$Value -isnot [bool] -or -not $Value" in package,
        "candidate package must reject string truthiness in JSON evidence")
require("installer_install_smoke = $installerStatus.installer_install_smoke" in package,
        "candidate evidence must copy the actual installer smoke result")
require("technical version $version is already published from a different immutable source" in workflow
        and "expectedSourceName" in workflow and "targetCommitish" in workflow,
        "public release idempotence must verify exact immutable source identity")
for field in ("public_beta_ready", "installer_install_smoke", "root_launcher_smoke"):
    require(f"Assert-TrueBoolean $installerStatus.{field}" in workflow,
            f"publication workflow strict boolean check missing: {field}")

print("PASS: root launcher + tidy Runtime/Uninstall installer / legacy migration / protected-data / updater-target contracts")
