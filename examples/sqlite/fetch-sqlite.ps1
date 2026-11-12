# Downloads SQLite's x64 DLL and its header from sqlite.org into vendor/windows, checks each archive
# against the SHA3-256 hash that sqlite.org's download page lists, and makes the import library
# sqlite3.lib from sqlite3.def with the lib.exe of the newest Visual Studio installation.
# Usage: pwsh examples/sqlite/fetch-sqlite.ps1

$ErrorActionPreference = 'Stop'

$archives = @(
    @{ Name = 'sqlite-dll-win-x64-3540100.zip'; Hash = 'c2d09c8babb9399277055c057c97a53f1ffd7c5f11d0c1f2118ffdfa39f2daa4' },
    @{ Name = 'sqlite-amalgamation-3540100.zip'; Hash = '92281c86e2957f9b9305890f9ac04204caa7b677cc868dc326d942689df28e30' }
)

$vendor = Join-Path $PSScriptRoot 'vendor/windows'
$download = Join-Path ([System.IO.Path]::GetTempPath()) "sqlite-fetch-$([guid]::NewGuid())"
New-Item -ItemType Directory -Force $vendor, $download | Out-Null

try {
    foreach ($archive in $archives) {
        $zip = Join-Path $download $archive.Name
        Invoke-WebRequest "https://sqlite.org/2026/$($archive.Name)" -OutFile $zip
        $hash = [Convert]::ToHexStringLower([System.Security.Cryptography.SHA3_256]::HashData([System.IO.File]::ReadAllBytes($zip)))
        if ($hash -ne $archive.Hash) {
            throw "$($archive.Name) has the SHA3-256 hash $hash, not $($archive.Hash)"
        }
        Expand-Archive $zip -DestinationPath (Join-Path $download 'files') -Force
    }

    $files = Join-Path $download 'files'
    foreach ($name in 'sqlite3.dll', 'sqlite3.def', 'sqlite3.h') {
        $found = Get-ChildItem $files -Recurse -Filter $name | Select-Object -First 1
        if (-not $found) { throw "the archives hold no $name" }
        Copy-Item $found.FullName $vendor -Force
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installation) { throw 'no Visual Studio installation has the C++ x64 tools' }
    $lib = Get-ChildItem (Join-Path $installation 'VC/Tools/MSVC') -Directory |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'bin/Hostx64/x64/lib.exe' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $lib) { throw "no lib.exe under $installation" }

    & $lib /nologo "/def:$(Join-Path $vendor 'sqlite3.def')" /machine:x64 "/out:$(Join-Path $vendor 'sqlite3.lib')"
    if ($LASTEXITCODE -ne 0) { throw "lib.exe exited with status $LASTEXITCODE" }
    Remove-Item (Join-Path $vendor 'sqlite3.exp') -ErrorAction SilentlyContinue
    Write-Host "SQLite is in $vendor"
}
finally {
    Remove-Item $download -Recurse -Force -ErrorAction SilentlyContinue
}
