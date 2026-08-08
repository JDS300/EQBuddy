# EQBuddy release: publish exe, sign, compile installer, sign it, refresh zip,
# push to OneDrive (the family's install + auto-update channel).
# Commit + `git push` your source changes too; git is the source-code backup.
param([string]$Tag)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$oneDrive = 'C:\Users\david\OneDrive\EQBuddyDownload'

# Version comes from Directory.Build.props (single source for BOTH apps — issue #30:
# a separate Avalonia version shipped stale Linux builds) so the apps, installer, and
# updater always agree.
$props = Get-Content "$repo\Directory.Build.props" -Raw
if ($props -notmatch '<Version>([\d.]+)</Version>') { throw 'No <Version> in Directory.Build.props' }
$version = $Matches[1]
Write-Host "Releasing EQBuddy $version"

# The in-app "What's new" popup reads embedded notes; a release without an entry
# would show users nothing. Refuse rather than rot.
$whatsNew = Get-Content "$repo\src\EQBuddy.Core\Data\WhatsNew.json" -Raw | ConvertFrom-Json
if (-not ($whatsNew | Where-Object { $_.version -eq $version })) {
    throw "No What's-new entry for $version in src\EQBuddy.Core\Data\WhatsNew.json — add one before releasing."
}

# Remember the running app so it comes back at the end — this kill used to be silent,
# and David's widget vanished mid-fight when v1.39.0 shipped (2026-08-07).
$runningApp = (Get-Process EQBuddy -ErrorAction SilentlyContinue | Select-Object -First 1).Path
Get-Process EQBuddy -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

dotnet publish "$repo\src\EQBuddy\EQBuddy.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$repo\dist\publish"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Sign with the self-signed EQBuddy cert if present (create once with scripts\new-cert.ps1).
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -like '*EQBuddy*' } | Select-Object -First 1
function Sign-File($path) {
    if (-not $cert) { Write-Warning "No EQBuddy code-signing cert found; skipping signature for $path"; return }
    $r = Set-AuthenticodeSignature -FilePath $path -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    # Self-signed cert => Status is UnknownError (untrusted root) on machines that
    # haven't imported dist\EQBuddy-publisher.cer; the signature itself is embedded.
    $ok = $r.SignerCertificate -ne $null
    Write-Host "Signed $(Split-Path $path -Leaf): $(if ($ok) { 'signature embedded' } else { $r.Status })"
}
Sign-File "$repo\dist\publish\EQBuddy.exe"

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) not found' }
& $iscc "/DAppVersion=$version" "$repo\installer\EQBuddy.iss"
if ($LASTEXITCODE -ne 0) { throw 'installer compile failed' }
Sign-File "$repo\dist\EQBuddySetup.exe"

Compress-Archive -Path "$repo\dist\publish\EQBuddy.exe", "$repo\README.md" `
    -DestinationPath "$repo\dist\EQBuddy-portable.zip" -Force

# Publish SHA-256 alongside the installer; the in-app updater refuses a
# staged installer that doesn't match (UPDATE-003).
(Get-FileHash "$repo\dist\EQBuddySetup.exe" -Algorithm SHA256).Hash |
    Set-Content "$repo\dist\EQBuddySetup.exe.sha256" -NoNewline

New-Item -ItemType Directory -Force $oneDrive | Out-Null
Copy-Item "$repo\dist\EQBuddySetup.exe", "$repo\dist\EQBuddySetup.exe.sha256", "$repo\dist\EQBuddy-portable.zip" $oneDrive -Force
Write-Host "Released $version to $oneDrive (family widgets will offer the update within 6 h)"

if ($Tag) {
    # Issue #56 (sahaq): `gh release create` tags whatever GitHub-side main happens to
    # be — if the release commit was never pushed, the tag lands on the PREVIOUS
    # release's commit and CI ships a stale Linux binary under the new version number.
    # So: push first, tag HEAD explicitly, push the tag, and refuse to release unless
    # the tag's own Directory.Build.props agrees with the version being released.
    git push origin main
    if ($LASTEXITCODE -ne 0) { throw 'git push failed - the release commit must be on origin/main' }
    git tag $Tag
    if ($LASTEXITCODE -ne 0) { throw "git tag $Tag failed (already exists? delete it or pick the next version)" }
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "pushing tag $Tag failed" }
    $tagProps = git show "${Tag}:Directory.Build.props" | Out-String
    if ($tagProps -notmatch [regex]::Escape("<Version>$version</Version>")) {
        throw "Tag $Tag does not contain <Version>$version</Version> - refusing to release a mismatched build"
    }
    gh release create $Tag "$repo\dist\EQBuddySetup.exe" "$repo\dist\EQBuddySetup.exe.sha256" "$repo\dist\EQBuddy-portable.zip" `
        --title "EQBuddy $Tag" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw 'gh release failed' }
    Write-Host "GitHub release $Tag published"
}

if ($runningApp -and (Test-Path $runningApp)) {
    Start-Process -FilePath $runningApp | Out-Null
    Write-Host "Relaunched $runningApp"
}
