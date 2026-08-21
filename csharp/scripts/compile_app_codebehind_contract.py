#!/usr/bin/env python3
"""Compile all WinUI code-behind with a generated XAML field surface.

This catches C# type/API/handler errors on non-Windows hosts. It deliberately does
not replace the real Windows XAML/XBF/PRI build performed by verify.ps1.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "csharp/src/BiliSubStudio.App"
CORE = ROOT / "csharp/src/BiliSubStudio.Core/BiliSubStudio.Core.csproj"
XAML = "http://schemas.microsoft.com/winfx/2006/xaml"
PRESENTATION = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"


def control_type(tag: str) -> str:
    if not tag.startswith("{"):
        raise ValueError(f"named XAML element has no namespace: {tag}")
    namespace, local = tag[1:].split("}", 1)
    if namespace == PRESENTATION:
        return f"Microsoft.UI.Xaml.Controls.{local}"
    if namespace.startswith("using:"):
        return f"{namespace.removeprefix('using:')}.{local}"
    raise ValueError(f"unsupported named XAML namespace: {namespace} ({local})")


def generate_stubs() -> str:
    lines = [
        "// Generated compile-only XAML surface.",
        "#nullable enable",
        "#pragma warning disable CS0414",
        "",
    ]
    for path in sorted(APP.rglob("*.xaml")):
        root = ET.parse(path).getroot()
        qualified_name = root.attrib.get(f"{{{XAML}}}Class")
        if not qualified_name:
            continue
        namespace, class_name = qualified_name.rsplit(".", 1)
        lines.extend([
            f"namespace {namespace}",
            "{",
            f"    public partial class {class_name}",
            "    {",
            "        private void InitializeComponent() { }",
        ])
        for element in root.iter():
            name = element.attrib.get(f"{{{XAML}}}Name")
            if name:
                lines.append(f"        private global::{control_type(element.tag)} {name} = null!;")
        lines.extend(["    }", "}", ""])
    return "\n".join(lines)


def project_text(stubs: Path) -> str:
    app = APP.as_posix()
    core = CORE.as_posix()
    generated = stubs.as_posix()
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <UseWinUI>false</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
    <SelfContained>false</SelfContained>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{app}/**/*.cs" Exclude="{app}/**/bin/**/*.cs;{app}/**/obj/**/*.cs" />
    <Compile Include="{generated}" />
    <ProjectReference Include="{core}" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
  </ItemGroup>
</Project>
"""


def run(command: list[str], environment: dict[str, str]) -> None:
    completed = subprocess.run(command, cwd=ROOT, env=environment, check=False)
    if completed.returncode:
        raise SystemExit(completed.returncode)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", default="dotnet", help="path to the exact dotnet host")
    args = parser.parse_args()
    environment = os.environ.copy()
    environment.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    environment.setdefault("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
    environment.setdefault("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1")
    environment.setdefault("MSBUILDDISABLENODEREUSE", "1")
    with tempfile.TemporaryDirectory(prefix="bilisub-app-compile-contract-") as temporary:
        directory = Path(temporary)
        stubs = directory / "BiliSubStudio.XamlCompileStubs.g.cs"
        project = directory / "BiliSubStudio.App.CompileContracts.csproj"
        stubs.write_text(generate_stubs(), encoding="utf-8", newline="\n")
        project.write_text(project_text(stubs), encoding="utf-8", newline="\n")
        run([args.dotnet, "restore", str(project), "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true"], environment)
        run([
            args.dotnet, "msbuild", str(project), "-t:Compile", "-m:1",
            "-p:Configuration=Release", "-p:Platform=x64", "-p:EnableWindowsTargeting=true",
            "-p:UseSharedCompilation=false", "-p:NuGetAudit=false",
        ], environment)
    print("PASS: full WinUI code-behind compile-contract (real Windows XAML/XBF/PRI still required)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
