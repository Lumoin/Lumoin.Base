#!/usr/bin/env bash
set -euo pipefail

# Runs Stryker.NET mutation testing for each MSTest/Microsoft.Testing.Platform test
# project in turn, invoked from inside the test project directory. Every test project
# here references BOTH the dependency-free Lumoin.Base leaf AND its own specific
# project, so Stryker's project auto-detection is always ambiguous (it errors out
# with "Test project contains more than one project reference"); --project is passed
# explicitly per test project to select the project that project is actually testing.
#
# Usage:
#   ./run-stryker.sh                              # run for all three test projects
#   ./run-stryker.sh Lumoin.Base.Tests             # run for a single test project

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
config_file="$repo_root/stryker-config.json"
output_root="$repo_root/tempdocs/stryker"

test_projects=(
    "Lumoin.Base.Tests"
    "Lumoin.Base.Libsodium.Tests"
    "Lumoin.Base.MemoryProtection.Tests"
)

# A case lookup rather than an associative array: macOS ships bash 3.2, which has no declare -A.
mutation_project_for() {
    case "$1" in
        Lumoin.Base.Tests) echo "Lumoin.Base.csproj" ;;
        Lumoin.Base.Libsodium.Tests) echo "Lumoin.Base.Libsodium.csproj" ;;
        Lumoin.Base.MemoryProtection.Tests) echo "Lumoin.Base.MemoryProtection.csproj" ;;
        *) echo "No mutation project mapped for test project '$1'." >&2; return 1 ;;
    esac
}

if [[ $# -ge 1 ]]; then
    requested="$1"
    found=0
    for project in "${test_projects[@]}"; do
        if [[ "$project" == "$requested" ]]; then
            found=1
        fi
    done
    if [[ $found -eq 0 ]]; then
        echo "Unknown test project '$requested'. Expected one of: ${test_projects[*]}" >&2
        exit 1
    fi
    test_projects=("$requested")
fi

for project in "${test_projects[@]}"; do
    test_project_dir="$repo_root/test/$project"
    output_dir="$output_root/$project"

    mkdir -p "$output_dir"

    echo "Running Stryker for $project..."

    (
        cd "$test_project_dir"
        dotnet stryker --config-file "$config_file" --test-runner mtp --output "$output_dir" --project "$(mutation_project_for "$project")"
    )

    report="$(find "$output_dir" -name 'mutation-report.html' -print 2>/dev/null | sort | tail -n 1)"

    if [[ -n "$report" ]]; then
        echo "Stryker HTML report for $project: $report"
    else
        echo "Stryker finished for $project; no mutation-report.html found under $output_dir."
    fi
done
