#
# 설치 파일(setup.exe)을 만든다. tools/publish.ps1 를 먼저 실행해 배포 폴더가 있어야 한다.
#
#   powershell -ExecutionPolicy Bypass -File tools/publish.ps1 -Version 0.2.0
#   powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1 -Version 0.2.0
#
# 결과: artifacts/release/OtogeTrainer-<버전>-win-x64-setup.exe (+ .sha256)
#
# 요구: Inno Setup 6 (winget install JRSoftware.InnoSetup)
#
[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\OtogeTrainer.App\OtogeTrainer.App.csproj'
$script  = Join-Path $root 'installer\OtogeTrainer.iss'

if (-not $Version) {
    $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "버전을 정할 수 없습니다. -Version 으로 지정하세요." }
}
# 파일 속성(VersionInfoVersion)은 숫자만 받는다. 0.2.0-beta.1 → 0.2.0
$numeric = ($Version -split '[-+]')[0]

$outDir = Join-Path $root 'artifacts\release'
$source = Join-Path $outDir "OtogeTrainer-$Version-win-x64"
if (-not (Test-Path (Join-Path $source 'OtogeTrainer.exe'))) {
    throw "배포 폴더가 없습니다: $source — tools/publish.ps1 -Version $Version 를 먼저 실행하세요."
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) 을 찾을 수 없습니다. winget install JRSoftware.InnoSetup" }

& $iscc /Q "/DAppVersion=$Version" "/DAppVersionNumeric=$numeric" "/DSourceDir=$source" "/DOutputDir=$outDir" $script
if ($LASTEXITCODE) { throw "ISCC 실패 (exit $LASTEXITCODE)" }

$name  = "OtogeTrainer-$Version-win-x64-setup.exe"
$setup = Join-Path $outDir $name
$hash  = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
# sha256sum -c 형식으로, 줄 끝은 LF.
[IO.File]::WriteAllText("$setup.sha256", "$hash  $name`n", [Text.Encoding]::ASCII)

$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "완료: $setup ($sizeMb MB)"
Write-Host "SHA-256: $hash"
