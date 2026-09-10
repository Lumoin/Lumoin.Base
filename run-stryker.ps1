# Runs Stryker.NET mutation testing for each MSTest/Microsoft.Testing.Platform test
# project in turn, invoked from inside the test project directory. Every test project
# here references BOTH the dependency-free Lumoin.Base leaf AND its own specific
# project, so Stryker's project auto-detection is always ambiguous (it errors out
# with "Test project contains more than one project reference"); --project is passed
# explicitly per test project to select the project that project is actually testing.
#
# Usage:
#   .\run-stryker.ps1                              # run for all three test projects
#   .\run-stryker.ps1 Lumoin.Base.Tests             # run for a single test project

param(
    [string]$TestProject
)

$testProjects = @(
    'Lumoin.Base.Tests'
    'Lumoin.Base.Libsodium.Tests'
    'Lumoin.Base.MemoryProtection.Tests'
)

$mutationProjects = @{
    'Lumoin.Base.Tests'                  = 'Lumoin.Base.csproj'
    'Lumoin.Base.Libsodium.Tests'        = 'Lumoin.Base.Libsodium.csproj'
    'Lumoin.Base.MemoryProtection.Tests' = 'Lumoin.Base.MemoryProtection.csproj'
}

if($TestProject)
{
    if($testProjects -notcontains $TestProject)
    {
        Write-Error "Unknown test project '$TestProject'. Expected one of: $($testProjects -join ', ')."
        exit 1
    }
    $testProjects = @($TestProject)
}

$repoRoot = $PSScriptRoot
$configFile = Join-Path $repoRoot 'stryker-config.json'
$outputRoot = Join-Path $repoRoot 'tempdocs/stryker'

foreach($project in $testProjects)
{
    $testProjectDir = Join-Path $repoRoot "test/$project"
    $outputDir = Join-Path $outputRoot $project

    if(-not (Test-Path $outputDir))
    {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    Write-Host "Running Stryker for $project..."

    Push-Location $testProjectDir
    try
    {
        dotnet stryker --config-file $configFile --test-runner mtp --output $outputDir --project $mutationProjects[$project]
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }

    if($exitCode -ne 0)
    {
        Write-Error "Stryker failed for $project (exit code $exitCode)."
        exit $exitCode
    }

    $report = Get-ChildItem -Path $outputDir -Filter 'mutation-report.html' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if($report)
    {
        Write-Host "Stryker HTML report for $project`: $($report.FullName)"
    }
    else
    {
        Write-Host "Stryker finished for $project; no mutation-report.html found under $outputDir."
    }
}
