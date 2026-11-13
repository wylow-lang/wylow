# Downloads SQLite's amalgamation from sqlite.org, checks the archive against the SHA3-256 hash that
# sqlite.org's download page lists, and puts sqlite3.c and sqlite3.h in native/.
# Usage: pwsh examples/sqlite/sqlite/fetch-sqlite.ps1

$ErrorActionPreference = 'Stop'

$name = 'sqlite-amalgamation-3540100.zip'
$expected = '92281c86e2957f9b9305890f9ac04204caa7b677cc868dc326d942689df28e30'

$native = Join-Path $PSScriptRoot 'native'
$download = Join-Path ([System.IO.Path]::GetTempPath()) "sqlite-fetch-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force $native, $download | Out-Null

try {
    $zip = Join-Path $download $name
    Invoke-WebRequest "https://sqlite.org/2026/$name" -OutFile $zip
    $hash = [Convert]::ToHexStringLower([System.Security.Cryptography.SHA3_256]::HashData([System.IO.File]::ReadAllBytes($zip)))
    if ($hash -ne $expected) {
        throw "$name has the SHA3-256 hash $hash, not $expected"
    }
    $files = Join-Path $download 'files'
    Expand-Archive $zip -DestinationPath $files -Force
    foreach ($file in 'sqlite3.c', 'sqlite3.h') {
        $found = Get-ChildItem $files -Recurse -Filter $file | Select-Object -First 1
        if (-not $found) { throw "the archive holds no $file" }
        Copy-Item $found.FullName $native -Force
    }
    Write-Host "SQLite is in $native"
}
finally {
    Remove-Item $download -Recurse -Force -ErrorAction SilentlyContinue
}
