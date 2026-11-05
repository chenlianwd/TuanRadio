# TuanRadio release packaging script.
# Usage: powershell -File tools\publish.ps1 [-Version 1.0.0] [-PackInstaller]
# Version defaults to <Version> in AIRadio.Desktop.csproj.
# Produces: release\TuanRadio-v{version}-win-x64\ (staged folder, root layer of the zip)
#           release\TuanRadio-v{version}-win-x64.zip
#           release\TuanRadio-v{version}-win-x64.zip.sha256
# When -PackInstaller is specified, also produces in release\velopack\:
#           TuanRadio-win-Setup.exe (+ .sha256)
#           TuanRadio-{version}-full.nupkg
#           releases.win.json
# Also reused by .github/workflows/release.yml - keep it PowerShell 5.1 compatible
# (no ?? operator, no ternary) and ASCII-only to avoid codepage issues.

param(
    [string]$Version = "",
    [switch]$PackInstaller
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'AIRadio.Desktop\AIRadio.Desktop.csproj'
$configuration = 'Release'
$rid = 'win-x64'
$outDir = Join-Path $repoRoot 'release'
$publishDir = Join-Path $outDir 'publish'

if (-not $Version) {
    $csproj = [xml](Get-Content -LiteralPath $project)
    foreach ($group in $csproj.Project.PropertyGroup) {
        if ($group.Version) { $Version = $group.Version.Trim(); break }
    }
}
if (-not $Version) { throw "Version not resolved; pass -Version explicitly." }
$stageName = "TuanRadio-v$Version-$rid"

Write-Host "==> dotnet publish ($configuration, $rid, self-contained, full providers, v$Version)"
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $project -c $configuration -r $rid --self-contained -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# PDBs are not needed to run the app.
Get-ChildItem -LiteralPath $publishDir -Recurse -Filter '*.pdb' |
    Remove-Item -Force

# providers-manifest.json is written by an AfterTargets=Build Exec into $(TargetDir)
# (the RID bin folder), which publish does not pick up when -o overrides the output.
$manifestInPublish = Join-Path $publishDir 'providers-manifest.json'
if (-not (Test-Path -LiteralPath $manifestInPublish)) {
    $binManifest = Join-Path $repoRoot "AIRadio.Desktop\bin\$configuration\net10.0-windows10.0.19041.0\$rid\providers-manifest.json"
    if (Test-Path -LiteralPath $binManifest) {
        Copy-Item -LiteralPath $binManifest -Destination $manifestInPublish -Force
    } else {
        throw "providers-manifest.json not found at $binManifest (full providers build required for release archive)."
    }
}

# Ship license texts with the archive so offline users can read them.
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $publishDir -Recurse -Force

# Stage as a single top-level folder so extracting does not scatter files.
$stageDir = Join-Path $outDir $stageName
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
Move-Item -LiteralPath $publishDir -Destination $stageDir

$zipPath = Join-Path $outDir "$stageName.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$sevenZip = Get-Command '7z' -ErrorAction SilentlyContinue
if ($sevenZip) {
    Write-Host "==> compressing with 7z"
    # 7z keeps the staged folder name as the zip root layer.
    & 7z a -tzip $zipPath $stageDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z failed with exit code $LASTEXITCODE" }
} else {
    Write-Warning '7z not on PATH; falling back to Compress-Archive (slow on node_modules).'
    Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal
}

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zipPath)" | Out-File -FilePath "$zipPath.sha256" -Encoding ascii

$sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host "==> done: $zipPath ($sizeMb MB)"
Write-Host "SHA256: $hash"

if ($PackInstaller) {
    $vpkCmd = Get-Command 'vpk' -ErrorAction SilentlyContinue
    if ($vpkCmd) {
        $vpkExe = $vpkCmd.Source
    } else {
        $toolCandidate = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
        if (Test-Path -LiteralPath $toolCandidate) {
            $vpkExe = $toolCandidate
        } else {
            throw "vpk CLI not found on PATH or at $toolCandidate. Install with: dotnet tool install -g vpk"
        }
    }

    $vpkOutDir = Join-Path $outDir 'velopack'
    if (Test-Path -LiteralPath $vpkOutDir) { Remove-Item -LiteralPath $vpkOutDir -Recurse -Force }
    $iconPath = Join-Path $repoRoot 'AIRadio.Desktop\Assets\airadio.ico'

    Write-Host "==> vpk pack (TuanRadio v$Version)"
    & $vpkExe pack `
        --packId 'TuanRadio' `
        --packVersion $Version `
        --packDir $stageDir `
        --mainExe 'AIRadio.exe' `
        --packTitle 'TuanRadio FM' `
        --runtime $rid `
        --channel 'win' `
        --icon $iconPath `
        --outputDir $vpkOutDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

    $setupPath = Join-Path $vpkOutDir 'TuanRadio-win-Setup.exe'
    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Expected installer not found at $setupPath"
    }

    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  $(Split-Path -Leaf $setupPath)" | Out-File -FilePath "$setupPath.sha256" -Encoding ascii
    $setupSizeMb = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 1)
    Write-Host "==> installer done: $setupPath ($setupSizeMb MB)"
    Write-Host "Setup SHA256: $setupHash"
}
