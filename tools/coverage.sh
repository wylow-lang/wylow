#!/usr/bin/env bash
# Runs the test suite under dotnet-coverage in every mode (MSVC, g++, --no-opt, --no-nest, clang-cl
# with the sanitizers, and the IR interpreter) and writes, to tests/.coverage/:
#   summary.txt  coverage by layer and file, the lines no test ran, and the error, check, and
#                unreachable sites no test reached, merged over the modes
#   html/        ReportGenerator's report, to read the uncovered lines in their source
# Arguments go to every run of the test runner (a filter, ...). WYL_COVERAGE_MODES limits the modes,
# for a quicker look; a line uncovered then may be covered by a mode that didn't run.
# Needs: dotnet tool install -g dotnet-coverage; dotnet tool install -g dotnet-reportgenerator-globaltool
set -euo pipefail
cd "$(dirname "$0")/.."
out=tests/.coverage
rm -rf "$out"
mkdir -p "$out"

dotnet build --nologo -v quiet
# The precompiled header is cached between runs; a run that finds one never covers building it.
rm -rf tests/Wyl.Tests/bin/Debug/net10.0/pch
export DOTNET_COVERAGE_NOLOGO=1
declare -A modes=( [msvc]="" [gcc]="--cc gcc" [noopt]="--no-opt" [nonest]="--no-nest" [asan]="--cc asan" [interpret]="--interpret" )
for name in ${WYL_COVERAGE_MODES:-msvc gcc noopt nonest asan interpret}; do
    # Unquoted, so a mode's arguments split into words; an empty one adds none.
    dotnet-coverage collect -f cobertura -o "$out/$name.xml" -- \
        dotnet tests/Wyl.Tests/bin/Debug/net10.0/Wyl.Tests.dll ${modes[$name]} "$@" \
        || echo "the $name suite failed; coverage is of the run as it was"
done
reportgenerator -reports:"$out/*.xml" -targetdir:"$out/html" -reporttypes:Html -assemblyfilters:+Wyl.Compiler* -verbosity:Warning
dotnet run tools/coverage-report.cs -- --uncovered "$out"/*.xml . > "$out/summary.txt"
sed -n '1,/^$/p' "$out/summary.txt"
grep -A3 '^Sites' "$out/summary.txt"
echo "full report: $out/summary.txt and $out/html/index.html"
