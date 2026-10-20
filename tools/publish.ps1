#
# 배포용 zip 을 만든다. 로컬에서도, GitHub Actions 릴리스 워크플로에서도 이 스크립트 하나로 만든다.
#
#   powershell -ExecutionPolicy Bypass -File tools/publish.ps1                 # 버전은 csproj 의 <Version>
#   powershell -ExecutionPolicy Bypass -File tools/publish.ps1 -Version 0.2.0
#
# 결과: artifacts/release/OtogeTrainer-<버전>-win-x64.zip (+ .sha256)
#
# self-contained 단일 파일로 내보낸다 — 사용자 PC 에 .NET 이 없어도 되고, 폴더에는 exe 와 네이티브 DLL 몇 개만 남는다.
# libmpv-2.dll 은 단일 파일에 묶지 않는다. 120MB 를 실행할 때마다 임시 폴더에 푸는 셈이 되기 때문.
#
[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\OtogeTrainer.App\OtogeTrainer.App.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "버전을 정할 수 없습니다. -Version 으로 지정하세요." }
}

$name    = "OtogeTrainer-$Version-win-x64"
$outRoot = Join-Path $root 'artifacts\release'
$stage   = Join-Path $outRoot $name
$zip     = Join-Path $outRoot "$name.zip"

& (Join-Path $PSScriptRoot 'fetch-libmpv.ps1')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip)   { Remove-Item -Force $zip }

dotnet publish $project `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:Version=$Version `
    -o $stage
if ($LASTEXITCODE) { throw "dotnet publish 실패 (exit $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $stage 'libmpv-2.dll'))) { throw "출력에 libmpv-2.dll 이 없습니다." }

Copy-Item (Join-Path $root 'LICENSE')               (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $stage 'THIRD-PARTY-NOTICES.md')

# Compress-Archive 는 수백 MB 에서 매우 느리다.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
# sha256sum -c 형식으로, 줄 끝은 LF. CRLF 면 sha256sum 이 파일 이름 끝의 CR 때문에 파일을 찾지 못한다.
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n", [Text.Encoding]::ASCII)

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "완료: $zip ($sizeMb MB)"
Write-Host "SHA-256: $hash"
