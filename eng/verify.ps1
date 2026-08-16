[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$IncludeInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-DotNet @(
            'restore', 'Piper.slnx',
            '-p:NuGetAudit=true',
            '-p:NuGetAuditMode=all',
            '-p:NuGetAuditLevel=moderate',
            '-warnaserror:NU1902;NU1903;NU1904'
        )
    }

    Invoke-DotNet @(
        'format', 'analyzers', 'Piper.slnx',
        '--verify-no-changes', '--no-restore', '--severity', 'warn'
    )

    $projects = @(
        'src/Piper.App/Piper.App.csproj',
        'tests/Piper.SmokeTests/Piper.SmokeTests.csproj',
        'tools/Piper.TrafficGen/Piper.TrafficGen.csproj'
    )

    foreach ($project in $projects) {
        Invoke-DotNet @(
            'build', $project,
            '--configuration', 'Release',
            '--no-restore',
            '-p:TreatWarningsAsErrors=true'
        )
    }

    if ($IncludeInstaller) {
        Invoke-DotNet @(
            'build', 'installer/Piper.Installer.csproj',
            '--configuration', 'Release',
            '--no-restore',
            '-p:TreatWarningsAsErrors=true'
        )
    }

    Invoke-DotNet @(
        'run',
        '--project', 'tests/Piper.SmokeTests/Piper.SmokeTests.csproj',
        '--configuration', 'Release',
        '--no-build'
    )

    Write-Host 'Verification passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
