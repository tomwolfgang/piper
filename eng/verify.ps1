[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$IncludeInstaller,
    # The UI tests open windows, so they need an interactive desktop session. CI always runs them.
    [switch]$SkipUiTests,
    # Restore and build only: no analyzer check and no tests. CodeQL (.github/workflows/security.yml)
    # uses it, so it compiles exactly the projects this gate builds.
    [switch]$BuildOnly
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
    # The benchmarks are deliberately not in Piper.slnx (nothing ships or tests them), so they are
    # restored and built on their own; building them here stops them from rotting unnoticed.
    $benchmarksProject = 'tests/Piper.Benchmarks/Piper.Benchmarks.csproj'

    if (-not $SkipRestore) {
        foreach ($restoreTarget in @('Piper.slnx', $benchmarksProject)) {
            Invoke-DotNet @(
                'restore', $restoreTarget,
                '-p:NuGetAudit=true',
                '-p:NuGetAuditMode=all',
                '-p:NuGetAuditLevel=moderate',
                '-warnaserror:NU1902;NU1903;NU1904'
            )
        }
    }

    if (-not $BuildOnly) {
        Invoke-DotNet @(
            'format', 'analyzers', 'Piper.slnx',
            '--verify-no-changes', '--no-restore', '--severity', 'warn'
        )
    }

    $projects = @(
        'src/Piper.App/Piper.App.csproj',
        'tests/Piper.SmokeTests/Piper.SmokeTests.csproj',
        'tests/Piper.UiTests/Piper.UiTests.csproj',
        'tools/Piper.TrafficGen/Piper.TrafficGen.csproj',
        'tools/Piper.Bench/Piper.Bench.csproj',
        $benchmarksProject
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

    if ($BuildOnly) {
        Write-Host 'Build passed (-BuildOnly: analyzer check and tests skipped).' -ForegroundColor Green
        return
    }

    Invoke-DotNet @(
        'run',
        '--project', 'tests/Piper.SmokeTests/Piper.SmokeTests.csproj',
        '--configuration', 'Release',
        '--no-build'
    )

    if ($SkipUiTests) {
        Write-Host 'Skipping UI tests (-SkipUiTests).' -ForegroundColor Yellow
    }
    else {
        Invoke-DotNet @(
            'run',
            '--project', 'tests/Piper.UiTests/Piper.UiTests.csproj',
            '--configuration', 'Release',
            '--no-build'
        )
    }

    Write-Host 'Verification passed.' -ForegroundColor Green
}
finally {
    Pop-Location
}
