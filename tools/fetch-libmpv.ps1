#
# libmpv 를 받아 native/libmpv-2.dll 에 둔다.
#
# DLL 은 120MB 라 저장소에 넣지 않는다(.gitignore). 개발 · 릴리스 빌드 모두 이 스크립트로 같은 빌드를 받는다.
# 버전을 올릴 때는 아래 세 값을 함께 바꾸고 THIRD-PARTY-NOTICES.md 의 버전 · 커밋도 고친다.
#
#   powershell -ExecutionPolicy Bypass -File tools/fetch-libmpv.ps1
#
# 요구: Windows 10 1803+ (내장 tar.exe 가 .7z 를 푼다)
#
[CmdletBinding()]
param(
    # 이미 같은 DLL 이 있어도 다시 받는다.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 의 진행 표시줄은 다운로드를 크게 느리게 만든다

# shinchiro/mpv-winbuild-cmake 릴리스 20260903 — mpv 커밋 69e63f425a
$ArchiveUrl    = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260903/mpv-dev-x86_64-20260903-git-69e63f425a.7z'
$ArchiveSha256 = 'fac135c68a35b7639e39d72c0c365104edbaebdea39a0dfdd8c36e8c8e80faef'
$DllSha256     = '673e6397920ab64a9c5b3a618f7f16d38854efe72b58665f1f84e4e873b763a4'

$root   = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root 'native'
$dll    = Join-Path $native 'libmpv-2.dll'

if (-not $Force -and (Test-Path $dll) -and (Get-FileHash $dll -Algorithm SHA256).Hash -eq $DllSha256) {
    Write-Host "libmpv 이미 있음: $dll"
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("otoge-libmpv-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $archive = Join-Path $work 'mpv-dev.7z'
    Write-Host "받는 중: $ArchiveUrl"
    Invoke-WebRequest -Uri $ArchiveUrl -OutFile $archive -UseBasicParsing

    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($actual -ne $ArchiveSha256) { throw "아카이브 해시 불일치: $actual (기대값 $ArchiveSha256)" }

    # Git for Windows 의 tar 는 .7z 를 풀지 못하므로 Windows 내장 tar 를 직접 지정한다.
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    & $tar -xf $archive -C $work libmpv-2.dll
    if ($LASTEXITCODE -ne 0) { throw "압축 해제 실패 (tar exit $LASTEXITCODE)" }

    $extracted = Join-Path $work 'libmpv-2.dll'
    $actual = (Get-FileHash $extracted -Algorithm SHA256).Hash
    if ($actual -ne $DllSha256) { throw "DLL 해시 불일치: $actual (기대값 $DllSha256)" }

    New-Item -ItemType Directory -Force -Path $native | Out-Null
    Copy-Item $extracted $dll -Force
    Write-Host "libmpv 배치 완료: $dll"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
