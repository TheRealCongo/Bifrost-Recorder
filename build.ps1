# Builds Bifrost Recorder with Windows' own C# compiler (C# 5, .NET Framework 4.x): nothing to install.
#   build\BifrostRecorder.exe   the Recorder
#   build\*.dll, build\obs\     libobs and the parts of it the Recorder uses, copied unmodified from OBS Studio's official release (see THIRD-PARTY-NOTICES.md)
#   build\LICENSE, build\THIRD-PARTY-NOTICES.md, build\licenses\   the licenses that travel with the program
#   build\RecorderTests.exe     the automated tests (test only); build\testbin\ holds ffprobe for them, from Bifrost Studio (test only)
# The OBS release is fetched once into deps\ (not in git) and checked against its published SHA-256 before anything is used.
# -Test runs build\RecorderTests.exe after building and fails if any test does. -Version sets the program's version.
# Works in Windows PowerShell 5.1 and PowerShell 7.
param([string]$Version = "0.2.0", [switch]$Test, [switch]$SkipRuntime)
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
  $cscArgs = @("/nologo", "/optimize+", "/codepage:65001", "/platform:x64", "/target:$target", "/out:$file") + $refs + $extra + $sources
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
[assembly: AssemblyCopyright("Copyright (C) 2026 TheRealCongo. GPL-2.0-or-later; see THIRD-PARTY-NOTICES.md")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Path $file -Encoding UTF8
  return $file
}

# libobs talks to native 64-bit DLLs, so the Recorder is built for x64 only.
$app = Get-ChildItem (Join-Path $root "src\Recorder") -Filter *.cs | ForEach-Object FullName
$versionFile = VersionFile "Bifrost Recorder" "Records a game or program, light on your PC"
try { Compile "winexe" (Join-Path $build "BifrostRecorder.exe") ($app + $versionFile) @(("/win32icon:" + (Join-Path $root "assets\icon.ico"))) }   # windows subsystem: it never has a console
finally { Remove-Item $versionFile -ErrorAction SilentlyContinue }

# ---- the OBS runtime: OBS Studio's official Windows release, pinned by version and SHA-256, copied unmodified
$obsVersion = "32.2.2"
$obsZipName = "OBS-Studio-$obsVersion-Windows-x64.zip"
$obsZipSha = "4D6E40E3AB155F56B30DE517380566A206D74B63CDF5AD49AA596924768F97E1"
$obsSrcName = "OBS-Studio-$obsVersion-Sources.tar.gz"   # the matching source, attached to each Recorder release for the GPL
$obsSrcSha = "EC81FB66B03E75DDB3076B576F62679C39262E0E9960CEF3E17A40DC5D68E6B4"
$obsUrl = "https://github.com/obsproject/obs-studio/releases/download/$obsVersion"
$deps = Join-Path $root "deps\obs-$obsVersion"

# Next to the exe: obs.dll and every library it and the plugins load, plus the helper programs libobs looks for beside the running program.
$besideExe = "obs.dll", "libobs-d3d11.dll", "libobs-winrt.dll", "w32-pthreads.dll", "zlib.dll",
             "avcodec-62.dll", "avformat-62.dll", "avutil-60.dll", "swscale-9.dll", "swresample-6.dll", "avdevice-62.dll", "avfilter-11.dll",
             "librist.dll", "srt.dll", "libx264-164.dll", "libcurl.dll", "obs-nvenc-test.exe", "obs-amf-test.exe", "obs-qsv-test.exe"
# The plugins: capture (game, window, screen), sound (WASAPI), the AAC and AMF encoders (obs-ffmpeg), NVENC, Quick Sync, x264, the MP4 output.
$plugins = "win-capture", "win-wasapi", "obs-ffmpeg", "obs-nvenc", "obs-qsv11", "obs-x264", "obs-outputs"

function Fetch([string]$name, [string]$sha) {
  $file = Join-Path $deps $name
  if (-not (Test-Path $file)) {
    New-Item -ItemType Directory -Force $deps | Out-Null
    Write-Host "Downloading $name from OBS Studio's GitHub release $obsVersion"
    Invoke-WebRequest -Uri "$obsUrl/$name" -OutFile "$file.part" -UseBasicParsing
    Move-Item "$file.part" $file -Force
  }
  $got = (Get-FileHash $file -Algorithm SHA256).Hash
  if ($got -ne $sha) { throw "$name has SHA-256 $got, not the published $sha. Delete it and build again." }
  return $file
}

function CopyIfChanged([string]$from, [string]$to) {
  if (-not (Test-Path $to) -or (Get-Item $to).Length -ne (Get-Item $from).Length -or (Get-Item $to).LastWriteTimeUtc -ne (Get-Item $from).LastWriteTimeUtc) {
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Copy-Item $from $to -Force
  }
}

if (-not $SkipRuntime) {
  $zip = Fetch $obsZipName $obsZipSha
  [void](Fetch $obsSrcName $obsSrcSha)
  $win = Join-Path $deps "win-x64"
  if (-not (Test-Path (Join-Path $win "bin\64bit\obs.dll"))) { Expand-Archive $zip $win -Force }
  foreach ($f in $besideExe) { CopyIfChanged (Join-Path $win "bin\64bit\$f") (Join-Path $build $f) }
  foreach ($p in $plugins) {
    CopyIfChanged (Join-Path $win "obs-plugins\64bit\$p.dll") (Join-Path $build "obs\obs-plugins\64bit\$p.dll")
    $data = Join-Path $win "data\obs-plugins\$p"
    if (Test-Path $data) { Get-ChildItem $data -Recurse -File | Where-Object Extension -ne ".pdb" | ForEach-Object { CopyIfChanged $_.FullName (Join-Path $build ("obs\data\obs-plugins\$p\" + $_.FullName.Substring($data.Length + 1))) } }
  }
  $core = Join-Path $win "data\libobs"
  Get-ChildItem $core -Recurse -File | ForEach-Object { CopyIfChanged $_.FullName (Join-Path $build ("obs\data\libobs\" + $_.FullName.Substring($core.Length + 1))) }
  # The game capture hook goes into games: it must be OBS's own, with its valid signature (anti-cheat software trusts that signature).
  foreach ($h in "graphics-hook64.dll", "graphics-hook32.dll", "inject-helper64.exe", "inject-helper32.exe") {
    $sig = Get-AuthenticodeSignature (Join-Path $build "obs\data\obs-plugins\win-capture\$h")
    if ($sig.Status -ne "Valid") { throw "$h is not validly signed ($($sig.Status)): the OBS files are not the official ones." }
  }
  $old = Join-Path $build "bin"   # the ffmpeg folder of the versions before 0.2.0
  if (Test-Path (Join-Path $old "ffmpeg.exe")) { Remove-Item $old -Recurse -Force }
  Write-Host "OBS Studio $obsVersion runtime ready in $build (hook signatures valid)"
}

# The licenses travel with the program.
foreach ($f in "LICENSE", "THIRD-PARTY-NOTICES.md") { Copy-Item (Join-Path $root $f) (Join-Path $build $f) -Force }
$lic = Join-Path $root "licenses"
Get-ChildItem $lic -Recurse -File | ForEach-Object { CopyIfChanged $_.FullName (Join-Path $build ("licenses\" + $_.FullName.Substring($lic.Length + 1))) }

# Test only: ffprobe to check the recordings, from the copy Bifrost Studio ships, kept apart from the Recorder's own libraries.
$studioBin = Join-Path $env:USERPROFILE "Bifrost Suite AppData\Bifrost Studio\resources\bin"
if (Test-Path (Join-Path $studioBin "ffprobe.exe")) {
  foreach ($f in (Get-ChildItem $studioBin -File)) { CopyIfChanged $f.FullName (Join-Path $build ("testbin\" + $f.Name)) }
} else { Write-Host "ffprobe not found in ${studioBin}: the tests that check recorded files will fail" }

# Test only: the same program code (without its Main) plus the tests and their animated window.
$testSources = ($app | Where-Object { (Split-Path $_ -Leaf) -ne "Program.cs" }) + (Join-Path $root "tests\RecorderTests.cs") + (Join-Path $root "tests\TestWindow.cs") + (Join-Path $root "tests\Diag.cs") + (Join-Path $root "tests\ArmoryStandIn.cs") + (Join-Path $root "tests\GpuSafety.cs") + (Join-Path $root "tests\Probe.cs")
Compile "exe" (Join-Path $build "RecorderTests.exe") $testSources

# FakeGame: a borderless fullscreen Direct3D 11 flip-model window that stands in for a game in the tests (test only).
Compile "winexe" (Join-Path $build "FakeGame.exe") @((Join-Path $root "tests\FakeGame.cs"))

if ($Test) {
  & (Join-Path $build "RecorderTests.exe")
  if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}
