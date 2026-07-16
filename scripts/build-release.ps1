[CmdletBinding()]
param(
    [string]$Version = '4.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishDir = Join-Path $repoRoot 'publish'
$artifactDir = Join-Path $repoRoot 'artifacts'
$stagingDir = Join-Path $artifactDir "ApkResourceAssistant-v$Version-win-x64"

foreach ($path in @($publishDir, $artifactDir)) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean path outside repository: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

dotnet run --project (Join-Path $repoRoot 'tests\ApkResourceAssistant.Tests\ApkResourceAssistant.Tests.csproj') -c Release
dotnet build (Join-Path $repoRoot 'ApkResourceAssistant.sln') -c Release --no-restore
dotnet publish (Join-Path $repoRoot 'src\ApkResourceAssistant\ApkResourceAssistant.csproj') `
    -c Release -r win-x64 --self-contained true --no-restore -o $publishDir

New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir 'ApkResourceAssistant.exe') -Destination $stagingDir
Copy-Item -LiteralPath (Join-Path $repoRoot '使用说明.md') -Destination $stagingDir
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $stagingDir

$exePath = Join-Path $stagingDir 'ApkResourceAssistant.exe'
$exeHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
"$exeHash  ApkResourceAssistant.exe" | Set-Content -LiteralPath (Join-Path $stagingDir 'SHA256.txt') -Encoding utf8NoBOM

$zipPath = Join-Path $artifactDir "ApkResourceAssistant-v$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$releaseExe = Join-Path $artifactDir 'ApkResourceAssistant.exe'
Copy-Item -LiteralPath $exePath -Destination $releaseExe
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash

@(
    "$exeHash  ApkResourceAssistant.exe"
    "$zipHash  ApkResourceAssistant-v$Version-win-x64.zip"
) | Set-Content -LiteralPath (Join-Path $artifactDir "ApkResourceAssistant-v$Version-SHA256.txt") -Encoding utf8NoBOM

Write-Host "Published: $releaseExe"
Write-Host "Package:   $zipPath"
Write-Host "EXE SHA256: $exeHash"
Write-Host "ZIP SHA256: $zipHash"
