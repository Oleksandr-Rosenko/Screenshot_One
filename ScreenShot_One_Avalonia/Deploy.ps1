Set-Location $PSScriptRoot



# Settings (bump version before each release)



$Version = "2.5.1"



$RepoUrl = "https://github.com/Oleksandr-Rosenko/Screenshot_One"







# Token lookup: env var, then .github_token file. Never hardcode the token here: this file is pushed to a public repo.



$Token = $env:GITHUB_TOKEN



if (-not $Token) { $Token = $env:GH_TOKEN }



$TokenFile = Join-Path $PSScriptRoot ".github_token"



if (-not $Token -and (Test-Path $TokenFile)) {



    $Token = (Get-Content $TokenFile -Raw).Trim()



}



if (-not $Token) {
    throw "GitHub token not found. Set the GITHUB_TOKEN environment variable or put the token into .github_token next to Deploy.ps1 (that file is in .gitignore and is never committed)."
}







Write-Host "=== 1. Publish project ===" -ForegroundColor Cyan



dotnet publish -f net10.0-windows -c Release -r win-x64 -p:Platform=x64 --self-contained true



if ($LASTEXITCODE -ne 0) { throw "Publish failed" }







Write-Host "=== 2. Create Velopack package ===" -ForegroundColor Cyan



vpk pack -u ScreenShotOne -v $Version -p "bin\x64\Release\net10.0-windows\win-x64\publish" -e Screenshot_One.exe



if ($LASTEXITCODE -ne 0) { throw "Pack failed" }







Write-Host "=== 3. Upload to GitHub ===" -ForegroundColor Cyan



vpk upload github --repoUrl $RepoUrl --token $Token --publish



if ($LASTEXITCODE -ne 0) { throw "Upload failed" }







Write-Host "=== 4. Update Release Notes on GitHub ===" -ForegroundColor Cyan
try {
    $NotesPy = Join-Path $PSScriptRoot "publish_notes.py"
    if (Test-Path $NotesPy) {
        python $NotesPy $Token $Version
    }
} catch {
    Write-Warning "Could not update release notes on GitHub: $_"
}

Write-Host "=== Done! Release $Version published to GitHub! ===" -ForegroundColor Green
