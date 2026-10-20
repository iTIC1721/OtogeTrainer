#
# 배포에 포함하는 libmpv 의 대응 소스(GPL)를 받아 zip 하나로 묶는다. 릴리스마다 함께 올린다.
#
#   powershell -ExecutionPolicy Bypass -File tools/bundle-libmpv-source.ps1
#
# 결과: artifacts/release/libmpv-source-20260903.zip
#
# libmpv 버전을 올릴 때는 fetch-libmpv.ps1 · 여기 · THIRD-PARTY-NOTICES.md 를 함께 고친다.
#
[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# shinchiro/mpv-winbuild-cmake 릴리스 20260903 을 이루는 소스
$BuildTag = '20260903'
$Sources = @(
    @{ Name = 'mpv-69e63f425a.tar.gz';                  Url = 'https://codeload.github.com/mpv-player/mpv/tar.gz/69e63f425a' },
    @{ Name = 'FFmpeg-9fc8c785e.tar.gz';                Url = 'https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/9fc8c785e' },
    @{ Name = "mpv-winbuild-cmake-$BuildTag.tar.gz";    Url = "https://codeload.github.com/shinchiro/mpv-winbuild-cmake/tar.gz/refs/tags/$BuildTag" }
)

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts\release' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$name  = "libmpv-source-$BuildTag"
$stage = Join-Path $OutDir $name
$zip   = Join-Path $OutDir "$name.zip"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip)   { Remove-Item -Force $zip }
New-Item -ItemType Directory -Path $stage | Out-Null

$sums = @()
foreach ($s in $Sources) {
    $file = Join-Path $stage $s.Name
    Write-Host "받는 중: $($s.Url)"
    Invoke-WebRequest -Uri $s.Url -OutFile $file -UseBasicParsing
    $sums += "$((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant())  $($s.Name)"
}
Set-Content -Path (Join-Path $stage 'SHA256SUMS') -Value $sums -Encoding Ascii

$readme = @"
libmpv corresponding source
===========================

This archive is the Corresponding Source (GNU GPL) for libmpv-2.dll shipped with OtogeTrainer.
libmpv-2.dll is an unmodified binary from shinchiro/mpv-winbuild-cmake release ${BuildTag}:
https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/$BuildTag
(mpv-dev-x86_64-$BuildTag-git-69e63f425a.7z)

Contents
- mpv-69e63f425a.tar.gz               mpv source at commit 69e63f425a
- FFmpeg-9fc8c785e.tar.gz             FFmpeg source at commit 9fc8c785e
- mpv-winbuild-cmake-$BuildTag.tar.gz  build scripts used to produce the binary.
                                       The other statically linked libraries (x264, x265, libass, ...)
                                       are fetched by these scripts from the repositories listed in packages/*.cmake.
- SHA256SUMS

If you need anything else to rebuild the binary, open an issue at
https://github.com/iTIC1721/OtogeTrainer/issues
"@
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -Encoding UTF8

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::NoCompression, $true)

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "완료: $zip ($sizeMb MB)"
