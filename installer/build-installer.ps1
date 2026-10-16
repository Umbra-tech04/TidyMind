# Builds the TidyMind installer: publishes the app (Release, win-x64, self-contained, ReadyToRun, not trimmed, not
# single-file, no .pdb files) and compiles TidyMind.iss with Inno Setup 6.
# Result: dist\TidyMind-Setup-<version>.exe, the version read from TidyMind.csproj.
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'TidyMind\TidyMind.csproj'
$script = Join-Path $PSScriptRoot 'TidyMind.iss'
$publishDir = Join-Path $PSScriptRoot 'obj\publish'
$distDir = Join-Path $root 'dist'

function Fail([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

# ---- Tools and inputs --------------------------------------------------

if (-not (Test-Path $project)) { Fail "Project not found: $project" }
if (-not (Test-Path $script)) { Fail "Inno Setup script not found: $script" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "The .NET SDK (dotnet) isn't installed. Get it from https://dotnet.microsoft.com/download"
}

$iscc = @($env:ISCC,
          (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
          (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}
if (-not $iscc) {
    Fail "Inno Setup 6 (ISCC.exe) wasn't found. Install it with:  winget install JRSoftware.InnoSetup  (or set the ISCC environment variable to the full path of ISCC.exe)."
}

$version = "$(& dotnet msbuild $project -nologo -getProperty:Version)".Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+') {
    Fail "Couldn't read the version from $project (got '$version')."
}

# ---- Publish -----------------------------------------------------------

# A clean folder, so nothing from an earlier build ends up in the installer.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Publishing TidyMind $version ..."
& dotnet publish $project -nologo -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:PublishSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed (see above)." }
if (-not (Test-Path (Join-Path $publishDir 'TidyMind.exe'))) { Fail "The publish output has no TidyMind.exe: $publishDir" }

# Some packages ship their own .pdb files; they aren't needed to run.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force

# ---- Installer ---------------------------------------------------------

New-Item -ItemType Directory -Force $distDir | Out-Null
$setup = Join-Path $distDir "TidyMind-Setup-$version.exe"
if (Test-Path $setup) { Remove-Item $setup -Force }

Write-Host "Compiling the installer with $iscc ..."
$output = & $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" "/O$distDir" "/FTidyMind-Setup-$version" $script 2>&1
$output | ForEach-Object { Write-Host "$_" }
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup couldn't compile $script (see above)." }
if ($output | Where-Object { "$_" -match '^\s*Warning' }) { Fail "Inno Setup reported warnings (see above); fix them before shipping." }
if (-not (Test-Path $setup)) { Fail "The installer wasn't created: $setup" }

Write-Host ("Done: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
