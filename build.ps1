# Builds Bifrost Recorder with Windows' own C# compiler (C# 5, .NET Framework 4.x): nothing to install.
#   build\BifrostRecorder.exe   the Recorder
#   build\bin\                  ffmpeg and ffprobe (gyan.dev 9.0.1 full, shared build) with its DLLs and licence, copied from Bifrost Studio
#   build\RecorderTests.exe     the automated tests (test only)
# -Test runs build\RecorderTests.exe after building and fails if any test does. -Version sets the program's version.
# Works in Windows PowerShell 5.1 and PowerShell 7.
param([string]$Version = "0.1.2", [switch]$Test, [switch]$SkipFfmpeg)
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$fw = Join-Path $env:SystemRoot "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
$build = Join-Path $root "build"
New-Item -ItemType Directory -Force $build | Out-Null

if ($Version -notmatch '^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$') { throw "Version must look like 0.1.0 or 0.1.0-beta.1" }
$numeric = ($Version -split '-')[0].Split('.')
while ($numeric.Count -lt 4) { $numeric += "0" }
$assemblyVersion = $numeric -join '.'

$refs = "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Web.Extensions.dll" |
        ForEach-Object { "/reference:" + (Join-Path $fw $_) }

function Compile([string]$target, [string]$file, [string[]]$sources, [string[]]$extra = @()) {
  $cscArgs = @("/nologo", "/optimize+", "/codepage:65001", "/target:$target", "/out:$file") + $refs + $extra + $sources
  $output = & $csc @cscArgs 2>&1
  if ($LASTEXITCODE -ne 0) { $output | ForEach-Object { Write-Host $_ }; throw "Compile failed: $file" }
  Write-Host "Built $file"
}

# The program's name and version, compiled in (Explorer's details; Task Manager shows the title).
function VersionFile([string]$title, [string]$description) {
  $file = Join-Path ([System.IO.Path]::GetTempPath()) ("BifrostVersion-" + [guid]::NewGuid().ToString("N") + ".cs")
  @"
using System.Reflection;
[assembly: AssemblyTitle("$title")]
[assembly: AssemblyProduct("Bifrost Suite")]
[assembly: AssemblyDescription("$description")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Path $file -Encoding UTF8
  return $file
}

$app = Get-ChildItem (Join-Path $root "src\Recorder") -Filter *.cs | ForEach-Object FullName
$versionFile = VersionFile "Bifrost Recorder" "Records a game or program, light on your PC"
try { Compile "winexe" (Join-Path $build "BifrostRecorder.exe") ($app + $versionFile) @(("/win32icon:" + (Join-Path $root "assets\icon.ico"))) }   # windows subsystem: it never has a console
finally { Remove-Item $versionFile -ErrorAction SilentlyContinue }

# ffmpeg, from the copy Bifrost Studio ships. Only files that are missing or differ are copied (the DLLs are 230 MB).
if (-not $SkipFfmpeg) {
  $from = Join-Path $env:USERPROFILE "Bifrost Suite AppData\Bifrost Studio\resources\bin"
  $to = Join-Path $build "bin"
  if (-not (Test-Path (Join-Path $from "ffmpeg.exe"))) { throw "ffmpeg.exe not found in $from" }
  New-Item -ItemType Directory -Force $to | Out-Null
  foreach ($f in (Get-ChildItem $from -File)) {
    $dest = Join-Path $to $f.Name
    if (-not (Test-Path $dest) -or (Get-Item $dest).Length -ne $f.Length -or (Get-Item $dest).LastWriteTimeUtc -ne $f.LastWriteTimeUtc) { Copy-Item $f.FullName $dest -Force }
  }
  Write-Host "ffmpeg ready in $to"
}

# Test only: the same program code (without its Main) plus the tests and their animated window.
$testSources = ($app | Where-Object { (Split-Path $_ -Leaf) -ne "Program.cs" }) + (Join-Path $root "tests\RecorderTests.cs") + (Join-Path $root "tests\TestWindow.cs") + (Join-Path $root "tests\Diag.cs") + (Join-Path $root "tests\ArmoryStandIn.cs")
Compile "exe" (Join-Path $build "RecorderTests.exe") $testSources

# FakeGame: a borderless fullscreen Direct3D 11 flip-model window that stands in for a game in the tests (test only).
Compile "winexe" (Join-Path $build "FakeGame.exe") @((Join-Path $root "tests\FakeGame.cs"))

if ($Test) {
  & (Join-Path $build "RecorderTests.exe")
  if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}
