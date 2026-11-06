# Builds a single, self-contained, obfuscated SocMacro.exe for distribution.
# Usage (from the SocMacro folder):  powershell -ExecutionPolicy Bypass -File publish_release.ps1
$ErrorActionPreference = "Stop"
$env:NODE_NO_WARNINGS = 1
Set-Location $PSScriptRoot

$rid = "win-x64"
$out = "publish"

Write-Host "빌드 + 난독화 + 단일 exe 생성 중... (몇 분 걸립니다)" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish -c Release -r $rid --self-contained true -o $out `
  -p:Obfuscate=true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

Get-ChildItem $out -Include *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
$exe = Get-Item "$out\SocMacro.exe"
Write-Host ""
Write-Host "완료: $($exe.FullName)" -ForegroundColor Green
Write-Host "크기: $([math]::Round($exe.Length/1MB,1)) MB" -ForegroundColor Green
Write-Host "이 exe 하나만 배포하면 됩니다. (.NET 설치 불필요, 관리자 권한으로 실행)" -ForegroundColor Green
