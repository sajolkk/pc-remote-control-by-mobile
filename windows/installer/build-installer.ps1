<#
.SYNOPSIS
    Publishes both PC-Remote executables and builds artifacts\release\PC-Remote-Setup.exe.

.DESCRIPTION
    Needs the .NET 10 SDK and Inno Setup 6. Inno Setup can be installed with:
        winget install --id JRSoftware.InnoSetup -e --scope user

    The two projects are published one at a time: a solution-wide build can run out of memory
    on a small machine (see shared/docs/04-setup-and-connect.md).
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# Staged outside artifacts\release: only the finished Setup.exe is distributed, and a copy of
# PC-Remote running from an old release folder would otherwise lock the files.
$publishDir = Join-Path $repo 'artifacts\publish\PC-Remote'

foreach ($project in 'RemoteAgent.Service', 'RemoteAgent.Session') {
    Write-Host "Publishing $project..."
    dotnet publish (Join-Path $repo "windows\$project\$project.csproj") -c Release -r $Runtime -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e --scope user'
}

Write-Host 'Building the installer...'
& $iscc /Q "/DSourceDir=$publishDir" "/DOutputDir=$(Join-Path $repo 'artifacts\release')" (Join-Path $PSScriptRoot 'PC-Remote.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

Write-Host "Done: $(Join-Path $repo 'artifacts\release\PC-Remote-Setup.exe')"
