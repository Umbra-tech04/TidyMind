# Publishes a TidyMind release for the in-app update (TidyMind/Updates.cs):
#   1. checks everything first: source committed, GitHub CLI installed and signed in, the releases repository there,
#      this version not released yet and newer than the latest one, release notes written;
#   2. builds the installer (build-installer.ps1) -> dist\TidyMind-Setup-<version>.exe;
#   3. writes its SHA-256 next to it -> dist\TidyMind-Setup-<version>.exe.sha256 ("<hash>  <file name>");
#   4. creates the GitHub release v<version> in the releases repository with both files as assets and the notes
#      from installer\release-notes\<version>.md as its text (TidyMind shows that text as plain text).
# The version is TidyMind.csproj's <Version>; the repository is ReleaseOwner/ReleaseRepo in TidyMind\UpdateEndpoints.cs
# (read from there, so it's named in one place only).
#
#   powershell -ExecutionPolicy Bypass -File installer\publish-release.ps1 -DryRun   # everything but step 4
#   powershell -ExecutionPolicy Bypass -File installer\publish-release.ps1

param([switch]$DryRun)

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'TidyMind\TidyMind.csproj'
$endpoints = Join-Path $root 'TidyMind\UpdateEndpoints.cs'
$distDir = Join-Path $root 'dist'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

# ---- Checks (nothing is built or published until all pass) ----------------

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "The .NET SDK (dotnet) isn't installed." }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "The GitHub CLI (gh) isn't installed. Install it with:  winget install GitHub.cli  then run:  gh auth login"
}

$source = Get-Content $endpoints -Raw
$owner = [regex]::Match($source, 'ReleaseOwner\s*=\s*"([^"]+)"').Groups[1].Value
$repoName = [regex]::Match($source, 'ReleaseRepo\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $owner -or -not $repoName) { Fail "ReleaseOwner/ReleaseRepo weren't found in $endpoints." }
$repo = "$owner/$repoName"

$version = "$(& dotnet msbuild $project -nologo -getProperty:Version)".Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Couldn't read a version such as 1.2.3 from $project (got '$version')."
}
$tag = "v$version"

$notesFile = Join-Path $PSScriptRoot "release-notes\$version.md"
if (-not (Test-Path $notesFile)) { Fail "No release notes: write them in $notesFile (plain sentences; TidyMind shows them as plain text)." }
if (-not "$(Get-Content $notesFile -Raw)".Trim()) { Fail "The release notes are empty: $notesFile" }

# A release is built from committed source, so it's known what went into it.
if (Get-Command git -ErrorAction SilentlyContinue) {
    $changes = "$(& git -C $root status --porcelain)".Trim()
    if ($changes) {
        if ($DryRun) { Write-Host "Note: there are uncommitted changes; a real release needs them committed first." -ForegroundColor Yellow }
        else { Fail "There are uncommitted changes in $root. Commit them first, so the release is built from known source." }
    }
}

& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Fail "The GitHub CLI isn't signed in. Run:  gh auth login" }

& gh repo view $repo --json name *> $null
if ($LASTEXITCODE -ne 0) { Fail "The repository $repo wasn't found (or this account can't see it). Create it first (public, with a README)." }

& gh release view $tag --repo $repo --json tagName *> $null
if ($LASTEXITCODE -eq 0) { Fail "$tag is already released in $repo. Raise <Version> in TidyMind.csproj first." }

# Installed copies only take a version newer than theirs, so an older or equal one would reach nobody.
$latest = "$(& gh release view --repo $repo --json tagName --jq .tagName 2>$null)".Trim()
if ($LASTEXITCODE -eq 0 -and $latest -match '^v?(\d+\.\d+\.\d+)$') {
    if ([version]$version -le [version]$Matches[1]) { Fail "$tag isn't newer than the latest release ($latest) in $repo." }
    Write-Host "Latest release in ${repo}: $latest"
} else {
    Write-Host "No release in $repo yet: this is the first one."
}

# ---- Build -------------------------------------------------------------

& (Join-Path $PSScriptRoot 'build-installer.ps1')
if ($LASTEXITCODE -ne 0) { Fail "The installer build failed (see above)." }

$setupName = "TidyMind-Setup-$version.exe"
$setup = Join-Path $distDir $setupName
if (-not (Test-Path $setup)) { Fail "The installer wasn't found: $setup" }

# ---- Checksum ----------------------------------------------------------

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$setup.sha256"
# ASCII, one line, no BOM: "<hash>  <file name>", the form sha256sum writes and TidyMind reads.
[System.IO.File]::WriteAllText($checksum, "$hash  $setupName`n", (New-Object System.Text.ASCIIEncoding))
Write-Host ("SHA-256 {0}  ({1:N1} MB)" -f $hash, ((Get-Item $setup).Length / 1MB))

# ---- Release -----------------------------------------------------------

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run: no release was created. It would be:" -ForegroundColor Yellow
    Write-Host "  gh release create $tag `"$setup`" `"$checksum`" --repo $repo --title `"TidyMind $version`" --notes-file `"$notesFile`" --latest"
    exit 0
}

Write-Host "Creating the release $tag in $repo ..."
& gh release create $tag $setup $checksum --repo $repo --title "TidyMind $version" --notes-file $notesFile --latest
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (see above). Check the repository's Releases page before trying again." }

Write-Host "Done: https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
