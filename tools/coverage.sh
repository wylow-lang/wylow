#!/usr/bin/env bash
# Runs the test suite under dotnet-coverage and writes, to tests/.coverage/:
#   summary.txt  coverage by layer and file, and the error, check, and unreachable sites no test reached
#   html/        ReportGenerator's report, to read the uncovered lines in their source
# Arguments go to the test runner (--cc gcc, a filter, ...).
# Needs: dotnet tool install -g dotnet-coverage; dotnet tool install -g dotnet-reportgenerator-globaltool
set -euo pipefail
cd "$(dirname "$0")/.."
out=tests/.coverage
rm -rf "$out"
mkdir -p "$out"

dotnet build --nologo -v quiet
export DOTNET_COVERAGE_NOLOGO=1
dotnet-coverage collect -f cobertura -o "$out/coverage.xml" -- dotnet tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll "$@" \
    || echo "the suite failed; coverage is of the run as it was"
reportgenerator -reports:"$out/coverage.xml" -targetdir:"$out/html" -reporttypes:Html -assemblyfilters:+Vpl.Compiler* -verbosity:Warning
dotnet run tools/coverage-report.cs -- "$out/coverage.xml" . > "$out/summary.txt"
sed -n '1,/^$/p' "$out/summary.txt"
grep -A3 '^Sites' "$out/summary.txt"
echo "full report: $out/summary.txt and $out/html/index.html"
