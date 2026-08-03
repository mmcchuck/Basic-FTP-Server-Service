<#
.SYNOPSIS
    Publishes the app self-contained and (optionally) compiles the Inno Setup installer.

.EXAMPLE
    .\build.ps1                 # publish + build installer
    .\build.ps1 -SkipInstaller  # publish only
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'publish'

Write-Host "==> Restoring and building ($Configuration)" -ForegroundColor Cyan
dotnet build (Join-Path $root 'BasicFtpServer.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\BasicFtpServer.Tests\BasicFtpServer.Tests.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "==> Publishing self-contained to $publishDir" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

# Self-contained so target machines need no .NET runtime installed. Trimming is deliberately
# off: WinForms and the DPAPI/ACL paths use reflection that trimming does not see.
dotnet publish (Join-Path $root 'src\BasicFtpServer.App\BasicFtpServer.App.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -o $publishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $publishDir 'BasicFtpServer.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe to exist after publish." }
Write-Host "    $([math]::Round((Get-ChildItem -Recurse $publishDir | Measure-Object Length -Sum).Sum / 1MB, 1)) MB published"

if ($SkipInstaller) {
    Write-Host "==> Skipping installer" -ForegroundColor Yellow
    return
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup not found. Install it with:  winget install JRSoftware.InnoSetup"
    Write-Warning "The published files in '$publishDir' are still usable via --install-service."
    return
}

Write-Host "==> Building installer with $iscc" -ForegroundColor Cyan
& $iscc (Join-Path $root 'installer\setup.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

Get-ChildItem (Join-Path $root 'installer\Output') -Filter *.exe |
    ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Green }
