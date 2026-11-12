param(
    [ValidateSet("arm64", "x64", "all")]
    [string]$Arch = "arm64",
    [string]$Version = "2.8.2",
    [string]$Configuration = "Release",
    # Ідентичність підпису: "-" — ad-hoc; у CI — власний сертифікат (стабільний підпис → macOS пам'ятає
    # дозвіл «Запис екрана» між оновленнями). Береться з MAC_SIGN_IDENTITY, якщо не задано.
    [string]$SignIdentity = $env:MAC_SIGN_IDENTITY
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$ProjectFile = Join-Path $ScriptDir "ScreenShot_One_Avalonia.csproj"
$DistDir = Join-Path $ScriptDir "dist/macOS"
$OnMac = $IsMacOS
if ([string]::IsNullOrWhiteSpace($SignIdentity)) { $SignIdentity = "-" }

if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

# Статичні збірки ffmpeg для macOS (ffmpeg.martin-riedl.de): з ними запис і обрізка відео працюють
# одразу, без Homebrew. Якщо завантаження не вдалось — програма шукатиме ffmpeg із Homebrew, як раніше.
function Get-Ffmpeg([string]$targetArch, [string]$destDir) {
    $ffArch = if ($targetArch -eq "arm64") { "arm64" } else { "amd64" }
    $cacheDir = Join-Path $DistDir "ffmpeg-cache/$targetArch"
    $cached = Join-Path $cacheDir "ffmpeg"
    if (-not (Test-Path $cached)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
        $zip = Join-Path $cacheDir "ffmpeg.zip"
        $url = "https://ffmpeg.martin-riedl.de/redirect/latest/macos/$ffArch/release/ffmpeg.zip"
        Write-Host "Downloading ffmpeg ($ffArch): $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $zip -MaximumRetryCount 3 -RetryIntervalSec 5
            Expand-Archive -Path $zip -DestinationPath $cacheDir -Force
        } catch {
            Write-Warning "ffmpeg download failed: $($_.Exception.Message). Video will need Homebrew ffmpeg."
            return $false
        }
        $found = Get-ChildItem -Path $cacheDir -Recurse -File -Filter "ffmpeg" | Select-Object -First 1
        if (-not $found) { Write-Warning "ffmpeg binary not found in archive"; return $false }
        if ($found.FullName -ne $cached) { Move-Item $found.FullName $cached -Force }
    }
    Copy-Item $cached (Join-Path $destDir "ffmpeg") -Force
    if ($OnMac) { chmod +x (Join-Path $destDir "ffmpeg") }
    return $true
}

# Підпис: кожен Mach-O файл окремо, потім сам бандл. Без «hardened runtime» (йому потрібні
# entitlements для JIT .NET). Якщо власний сертифікат не спрацював — ad-hoc, щоб збірка не падала.
function Sign-Bundle([string]$bundleDir, [string]$identity) {
    # Службові атрибути (карантин, Finder info) codesign не пропускає
    xattr -cr $bundleDir
    $mainExe = Join-Path $bundleDir "Contents/MacOS/Screenshot_One"
    $macOSDir = Join-Path $bundleDir "Contents/MacOS"
    # macOS вважає кодом усе в Contents/MacOS, а .NET кладе туди й .dll/.json — тож підписуємо кожен файл
    # (рецепт Avalonia для macOS); поза MacOS — лише Mach-O
    $files = Get-ChildItem -Path $bundleDir -Recurse -File | Where-Object {
        $_.FullName -ne $mainExe -and ($_.FullName.StartsWith($macOSDir) -or ((& file -b $_.FullName) -match 'Mach-O'))
    }
    Write-Host "Signing $($files.Count) nested files"
    foreach ($f in $files) {
        $out = codesign --force --timestamp=none --sign $identity $f.FullName 2>&1
        if ($LASTEXITCODE -ne 0) { throw "codesign failed for $($f.FullName): $out" }
    }
    # Головний виконуваний файл підписується разом із бандлом
    $out = codesign --force --timestamp=none --sign $identity $bundleDir 2>&1
    if ($LASTEXITCODE -ne 0) { throw "codesign failed for bundle: $out" }
    $out = codesign --verify --strict --verbose=2 $bundleDir 2>&1
    if ($LASTEXITCODE -ne 0) { throw "codesign verify failed: $out" }
    Write-Host $out
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

    # Swift-утиліти в Contents/MacOS — потрібен swiftc (Xcode / Command Line Tools):
    #   ocr-helper     — OCR (Vision), macOS 11+;
    #   capture-helper — знімок екрана через ScreenCaptureKit, macOS 14+ (на старіших програма її не запускає,
    #                    мінімальна версія самої програми лишається 10.15)
    $swiftHelpers = @(
        @{ Name = "ocr-helper";     Source = "MacHelpers/ocr.swift";     MinOS = "11" },
        @{ Name = "capture-helper"; Source = "MacHelpers/capture.swift"; MinOS = "14" }
    )
    $swiftc = Get-Command swiftc -ErrorAction SilentlyContinue
    $swiftArch = if ($targetArch -eq "arm64") { "arm64" } else { "x86_64" }
    foreach ($h in $swiftHelpers) {
        $swiftSrc = Join-Path $ScriptDir $h.Source
        if ($swiftc -and (Test-Path $swiftSrc)) {
            $swiftTarget = "$swiftArch-apple-macos$($h.MinOS)"
            Write-Host "=== [macOS $targetArch] Building $($h.Name) ($swiftTarget) ===" -ForegroundColor Cyan
            swiftc -O -target $swiftTarget -o (Join-Path $macOSDir $h.Name) $swiftSrc
            if ($LASTEXITCODE -ne 0) { throw "$($h.Name) build failed" }
        } else {
            Write-Warning "swiftc not found: $($h.Name) will be missing in this build (OCR unavailable / capture falls back to CoreGraphics)"
        }
    }

    # ffmpeg усередині програми (запис і обрізка відео без Homebrew)
    Write-Host "=== [macOS $targetArch] Bundling ffmpeg ===" -ForegroundColor Cyan
    Get-Ffmpeg $targetArch $macOSDir | Out-Null

    # Copy Info.plist
    $plistSrc = Join-Path $ScriptDir "Info.plist"
    if (Test-Path $plistSrc) {
        Copy-Item -Path $plistSrc -Destination (Join-Path $contentsDir "Info.plist") -Force
    }

    # Іконка програми для Finder/Dock (Info.plist → CFBundleIconFile = icon.icns)
    $icnsSrc = Join-Path $ScriptDir "Assets/icon.icns"
    if (Test-Path $icnsSrc) {
        Copy-Item -Path $icnsSrc -Destination (Join-Path $resourcesDir "icon.icns") -Force
    }

    $icoSrc = Join-Path $ScriptDir "Assets/icon.ico"
    if (Test-Path $icoSrc) {
        Copy-Item -Path $icoSrc -Destination (Join-Path $resourcesDir "icon.ico") -Force
    }

    if ($OnMac) {
        Write-Host "=== [macOS $targetArch] Signing (identity: $(if ($SignIdentity -eq '-') { 'ad-hoc' } else { $SignIdentity })) ===" -ForegroundColor Cyan
        try { Sign-Bundle $bundleDir $SignIdentity }
        catch {
            if ($SignIdentity -eq "-") { throw }
            Write-Warning "Signing with '$SignIdentity' failed ($($_.Exception.Message)); falling back to ad-hoc"
            Sign-Bundle $bundleDir "-"
        }
    }

    Write-Host "=== [macOS $targetArch] 3. Compressing into ZIP (used by the in-app updater) ===" -ForegroundColor Cyan
    $zipFileName = "Screenshot_One-v$Version-macOS-$targetArch.zip"
    $zipPath = Join-Path $DistDir $zipFileName
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    if ($OnMac) {
        # ditto зберігає підпис, права й символічні посилання
        ditto -c -k --sequesterRsrc --keepParent $bundleDir $zipPath
        if ($LASTEXITCODE -ne 0) { throw "ditto failed" }
    } else {
        $packScript = Join-Path $ScriptDir "package_mac_zip.py"
        python $packScript $bundleDir $zipPath
    }

    Write-Host ">>> Built: $zipPath" -ForegroundColor Green
}

# Один інсталятор .pkg для всіх Mac (сам вибирає Apple Silicon чи Intel)
if ($OnMac -and $Arch -eq "all") {
    Write-Host "`n=== 4. Building universal installer (.pkg) ===" -ForegroundColor Cyan
    $pkgWork = Join-Path $DistDir "pkg-work"
    if (Test-Path $pkgWork) { Remove-Item $pkgWork -Recurse -Force }
    New-Item -ItemType Directory -Path $pkgWork -Force | Out-Null
    $scripts = Join-Path $ScriptDir "MacInstaller"
    chmod +x (Join-Path $scripts "preinstall") (Join-Path $scripts "postinstall")

    foreach ($targetArch in $Architectures) {
        $root = Join-Path $DistDir $targetArch
        $plist = Join-Path $pkgWork "component-$targetArch.plist"
        pkgbuild --analyze --root $root $plist
        if ($LASTEXITCODE -ne 0) { throw "pkgbuild --analyze failed" }
        # Не «переносимий»: інакше Installer може оновити стару копію деінде (наприклад, у «Завантаженнях»)
        plutil -replace "0.BundleIsRelocatable" -bool NO $plist
        $pkgOut = Join-Path $pkgWork "$targetArch.pkg"
        # --preserve-xattr: підписи не-Mach-O файлів зберігаються в розширених атрибутах
        pkgbuild --root $root --component-plist $plist --install-location /Applications `
            --identifier "com.screenshotone.app.$targetArch" --version $Version `
            --scripts $scripts --preserve-xattr $pkgOut
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "pkgbuild --preserve-xattr failed, retrying without it"
            pkgbuild --root $root --component-plist $plist --install-location /Applications `
                --identifier "com.screenshotone.app.$targetArch" --version $Version `
                --scripts $scripts $pkgOut
            if ($LASTEXITCODE -ne 0) { throw "pkgbuild failed for $targetArch" }
        }
    }

    $pkgPath = Join-Path $DistDir "Screenshot_One-v$Version-macOS.pkg"
    if (Test-Path $pkgPath) { Remove-Item $pkgPath -Force }
    productbuild --distribution (Join-Path $scripts "distribution.xml") --package-path $pkgWork $pkgPath
    if ($LASTEXITCODE -ne 0) { throw "productbuild failed" }
    Write-Host ">>> Built: $pkgPath" -ForegroundColor Green
}

Write-Host "`n=== macOS build completed successfully! ===" -ForegroundColor Green
Write-Host "Bundles created in: $DistDir" -ForegroundColor Yellow
Write-Host "First install: open Screenshot_One-v$Version-macOS.pkg (once: System Settings → Privacy & Security → Open Anyway)." -ForegroundColor Gray
