<#
.SYNOPSIS
  Publishes Cadence as a self-contained exe, then packages it as an installer and portable zip.

.DESCRIPTION
  Produces an exe with no runtime prerequisite, which is the whole point: a quota monitor that
  first asks you to install a framework will not get installed.

  The exe is then packaged with Velopack (pinned in .config/dotnet-tools.json) into dist\releases:
    CadenceApp-<arch>-Setup.exe     per-user installer: no admin prompt, Start menu and desktop
                                    shortcuts, an uninstall entry, updates in the background
    CadenceApp-<arch>-Portable.zip  the same app for running from a folder
    (the CLI is in neither; it stays in dist\<arch>\cli)
    *.nupkg, releases.<arch>.json   the update feed; upload the folder to a GitHub release

  ARM64 is a first-class target. Snapdragon X laptops are common, and x64 emulation of a
  GDI-heavy tray renderer is noticeably worse than running native.

.PARAMETER Arch
  win-x64 (default) or win-arm64. Each architecture is its own update channel, so an installed
  copy only ever updates to builds for its own processor.

.PARAMETER Version
  The version to stamp and package. Defaults to <Version> in Directory.Build.props. Every release
  needs a higher one than the last, or installed copies will not see it as an update.

.PARAMETER Output
  Where to place the published files. Defaults to dist\<arch>.

.PARAMETER SkipInstaller
  Publish the exe only, without packaging. Faster when only the exe is needed.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Arch = 'win-x64',

    [string]$Version = '',

    [string]$Output = '',

    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "dist\$Arch" }
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version }

$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' }
          else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

Write-Host "Publishing Cadence $Version for $Arch -> $Output" -ForegroundColor Cyan

# Trimming is deliberately off. WPF is not trim-safe, and the reflection used by XAML binding
# fails at runtime in ways that do not show up until a specific window is opened.
$common = @(
    '-c', 'Release'
    '-r', $Arch
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=embedded'
    "-p:Version=$Version"
)

& $dotnet publish (Join-Path $root 'src\Cadence.App\Cadence.App.csproj') @common '-o' $Output
if ($LASTEXITCODE -ne 0) { throw "App publish failed with exit code $LASTEXITCODE" }

# The CLI goes in its own folder. Windows filenames are case-insensitive, so publishing
# Cadence.exe and cadence.exe side by side silently leaves only whichever was written last,
# and the CLI, being second, would quietly replace the tray app.
$cliOutput = Join-Path $Output 'cli'
& $dotnet publish (Join-Path $root 'src\Cadence.Cli\Cadence.Cli.csproj') @common '-o' $cliOutput
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }

if (-not (Test-Path (Join-Path $Output 'Cadence.exe'))) {
    throw 'Cadence.exe is missing from the publish output.'
}

$releases = Join-Path $root 'dist\releases'

if (-not $SkipInstaller) {
    Write-Host ''
    Write-Host "Packaging installer -> $releases" -ForegroundColor Cyan

    # The id names the install folder, %LOCALAPPDATA%\<id>. It must not be "Cadence": that folder
    # already holds the usage history and logs, and Velopack replaces an install folder on install
    # and deletes it on uninstall.
    $pack = @(
        'vpk', 'pack'
        '--packId', 'CadenceApp'
        '--packVersion', $Version
        '--packTitle', 'Cadence'
        '--packAuthors', 'Fan Zhu'
        '--packDir', $Output
        '--mainExe', 'Cadence.exe'
        # The CLI stays out of the installer: being self-contained it carries its own runtime and
        # would double the download for a tool most people never run. It ships from dist\<arch>\cli.
        '--exclude', '(^|[\\/])cli[\\/]|\.pdb$'
        '--icon', (Join-Path $root 'src\Cadence.App\Assets\Cadence.ico')
        '--channel', $Arch
        '--runtime', $Arch
        '--outputDir', $releases
    )

    Push-Location $root
    try {
        & $dotnet tool restore | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE" }

        & $dotnet @pack
        if ($LASTEXITCODE -ne 0) { throw "Packaging failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
}

Write-Host ''
Write-Host 'Published:' -ForegroundColor Green
Get-ChildItem $Output -Filter *.exe -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($Output.Length).TrimStart('\')
    '  {0,-40} {1,8:N1} MB' -f $relative, ($_.Length / 1MB)
}

if (-not $SkipInstaller) {
    Get-ChildItem $releases -File | Where-Object { $_.Name -like "*$Version*" -or $_.Name -like "*-$Arch-*" } |
        Sort-Object Name | ForEach-Object { '  {0,-40} {1,8:N1} MB' -f "releases\$($_.Name)", ($_.Length / 1MB) }
}

Write-Host ''
Write-Host 'Next: sign before distributing.' -ForegroundColor Yellow
Write-Host 'An unsigned tray app that reads credential files looks exactly like malware to'
Write-Host 'SmartScreen, and to a reasonable person. vpk pack signs everything it packages when'
Write-Host 'given --azureTrustedSignFile (Azure Trusted Signing, the cheap route) or --signParams.'
