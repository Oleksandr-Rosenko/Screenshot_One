param(
    [ValidateSet("arm64", "x64", "all")]
    [string]$Arch = "arm64",
    [string]$Version = "2.7.2",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$ProjectFile = Join-Path $ScriptDir "ScreenShot_One_Avalonia.csproj"
$DistDir = Join-Path $ScriptDir "dist/macOS"

if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

$Architectures = if ($Arch -eq "all") { @("arm64", "x64") } else { @($Arch) }

foreach ($targetArch in $Architectures) {
    $rid = "osx-$targetArch"
    Write-Host "`n=== [macOS $targetArch] 1. Publishing .NET binary ($rid) ===" -ForegroundColor Cyan

    $publishOut = Join-Path $ScriptDir "bin/$Configuration/net10.0/$rid/publish"

    dotnet publish $ProjectFile -f net10.0 -r $rid -c $Configuration --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    $appBundleName = "Screenshot_One.app"
    $bundleDir = Join-Path $DistDir "$targetArch/$appBundleName"
    $contentsDir = Join-Path $bundleDir "Contents"
    $macOSDir = Join-Path $contentsDir "MacOS"
    $resourcesDir = Join-Path $contentsDir "Resources"

    Write-Host "=== [macOS $targetArch] 2. Constructing .app bundle layout ===" -ForegroundColor Cyan
    if (Test-Path $bundleDir) {
        Remove-Item -Path $bundleDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $macOSDir -Force | Out-Null
    New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null

    # Copy published files into Contents/MacOS
    Copy-Item -Path "$publishOut/*" -Destination $macOSDir -Recurse -Force

    # OCR-утиліта (Vision) — потрібен swiftc (Xcode / Command Line Tools)
    $swiftSrc = Join-Path $ScriptDir "MacHelpers/ocr.swift"
    $swiftc = Get-Command swiftc -ErrorAction SilentlyContinue
    if ($swiftc -and (Test-Path $swiftSrc)) {
        $swiftTarget = if ($targetArch -eq "arm64") { "arm64-apple-macos11" } else { "x86_64-apple-macos11" }
        Write-Host "=== [macOS $targetArch] Building ocr-helper ($swiftTarget) ===" -ForegroundColor Cyan
        swiftc -O -target $swiftTarget -o (Join-Path $macOSDir "ocr-helper") $swiftSrc
        if ($LASTEXITCODE -ne 0) { throw "ocr-helper build failed" }
    } else {
        Write-Warning "swiftc not found: OCR on macOS will be unavailable in this build"
    }

    # Copy Info.plist
    $plistSrc = Join-Path $ScriptDir "Info.plist"
    if (Test-Path $plistSrc) {
        Copy-Item -Path $plistSrc -Destination (Join-Path $contentsDir "Info.plist") -Force
    }

    # Copy icon if available
    # Іконка програми для Finder/Dock (Info.plist → CFBundleIconFile = icon.icns)
    $icnsSrc = Join-Path $ScriptDir "Assets/icon.icns"
    if (Test-Path $icnsSrc) {
        Copy-Item -Path $icnsSrc -Destination (Join-Path $resourcesDir "icon.icns") -Force
    }

    $icoSrc = Join-Path $ScriptDir "Assets/icon.ico"
    if (Test-Path $icoSrc) {
        Copy-Item -Path $icoSrc -Destination (Join-Path $resourcesDir "icon.ico") -Force
    }

    Write-Host "=== [macOS $targetArch] 3. Compressing into distributable ZIP ===" -ForegroundColor Cyan
    $zipFileName = "Screenshot_One-v$Version-macOS-$targetArch.zip"
    $zipPath = Join-Path $DistDir $zipFileName
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    $packScript = Join-Path $ScriptDir "package_mac_zip.py"
    python $packScript $bundleDir $zipPath

    Write-Host ">>> Built: $zipPath" -ForegroundColor Green
}

Write-Host "`n=== macOS build completed successfully! ===" -ForegroundColor Green
Write-Host "Bundles created in: $DistDir" -ForegroundColor Yellow
Write-Host "On macOS, to run after downloading:" -ForegroundColor Gray
Write-Host "  1. Unzip the archive" -ForegroundColor Gray
Write-Host "  2. Run: xattr -cr Screenshot_One.app (clears Gatekeeper quarantine)" -ForegroundColor Gray
Write-Host "  3. Open Screenshot_One.app" -ForegroundColor Gray
