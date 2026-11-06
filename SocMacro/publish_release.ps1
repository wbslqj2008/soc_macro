# Builds a single, self-contained, obfuscated SocMacro.exe for distribution.
# Usage: powershell -ExecutionPolicy Bypass -File publish_release.ps1 -Version 0.1.1
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$UpdateRepository = 'wbslqj2008/soc_macro',
    [ValidatePattern('^([a-fA-F0-9]{40})?$')][string]$ReleaseCommit = '',
    [switch]$Upload
)
$ErrorActionPreference = "Stop"
$env:NODE_NO_WARNINGS = 1
Set-Location $PSScriptRoot

$rid = "win-x64"
$out = Join-Path $PSScriptRoot 'publish'
$expectedOut = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'publish'))
if ([System.IO.Path]::GetFullPath($out) -ne $expectedOut) { throw '배포 출력 경로가 올바르지 않습니다.' }
if ($Upload) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI(gh)를 설치하고 gh auth login을 먼저 실행해 주세요.' }
    gh auth status
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI 로그인이 필요합니다.' }
}

Write-Host "빌드 + 난독화 + 단일 exe 생성 중... (몇 분 걸립니다)" -ForegroundColor Cyan
if (Test-Path -LiteralPath $out) {
    if ((Get-Item -LiteralPath $out).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw '배포 출력 폴더가 다른 경로로 연결되어 있어 중단합니다.'
    }
    Remove-Item -LiteralPath $out -Recurse -Force
}

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw '난독화 도구 복원 실패' }

dotnet publish -c Release -r $rid --self-contained true -o $out `
  -p:Obfuscate=true `
  "-p:Version=$Version" "-p:UpdateRepository=$UpdateRepository" `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

Get-ChildItem $out -Include *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
$exe = Get-Item "$out\SocMacro.exe"
$checksum = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  SocMacro.exe'
[System.IO.File]::WriteAllText((Join-Path $out 'SocMacro.exe.sha256'), $checksum + "`n", [System.Text.Encoding]::ASCII)
Write-Host ""
Write-Host "완료: $($exe.FullName)" -ForegroundColor Green
Write-Host "크기: $([math]::Round($exe.Length/1MB,1)) MB" -ForegroundColor Green
Write-Host "이 exe 하나만 배포하면 됩니다. (.NET 설치 불필요, 관리자 권한으로 실행)" -ForegroundColor Green
Write-Host "자동 업데이트 배포 시 SocMacro.exe와 SocMacro.exe.sha256을 같은 GitHub Release에 첨부하세요."
if ($Upload) {
    # Publish only after both assets exist, so clients never see an incomplete release.
    $releaseArgs = @('release', 'create', "v$Version", '--repo', $UpdateRepository, '--draft', '--title', "소오콘 매크로 v$Version", '--notes', "소오콘 매크로 $Version 업데이트")
    if ($ReleaseCommit) { $releaseArgs += @('--target', $ReleaseCommit) }
    gh @releaseArgs
    if ($LASTEXITCODE -ne 0) { throw '릴리스 초안 생성 실패' }
    gh release upload "v$Version" "$out\SocMacro.exe" "$out\SocMacro.exe.sha256" --repo $UpdateRepository
    if ($LASTEXITCODE -ne 0) { throw '릴리스 파일 업로드 실패. 초안을 확인해 주세요.' }
    gh release edit "v$Version" --repo $UpdateRepository --draft=false --latest
    if ($LASTEXITCODE -ne 0) { throw '릴리스 공개 실패. 초안을 확인해 주세요.' }
    Write-Host "배포 완료: https://github.com/$UpdateRepository/releases/tag/v$Version" -ForegroundColor Green
}
