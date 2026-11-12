# Testing on Linux through WSL

CI runs the TSan mode on a Linux runner, and every other mode on Windows. The development machine
has WSL 2 with Ubuntu 24.04, so the suite can also run on Linux locally. This work is not queued in
a slice. Do it when a Linux failure needs a local reproduction, or before the SQLite binding needs
its Linux build (`interop-slice-2.md`, step 4).

## What the machine has

- Ubuntu 24.04 is the default distribution, and the repository is visible at
  `/mnt/e/Repos/Wylow/wylow`.
- g++ 13.3, GNU ld, clang, the .NET 10 SDK, and `libsqlite3-dev` are installed.
- The clone that the steps below describe is at `~/wylow`.
- An agent runs a command in it from Windows with `wsl -d Ubuntu -- bash -lc "<command>"`. A command
  that needs `sudo` asks for the user's password, so the user runs the installation.

## Setting it up

1. The user installs the tools in Ubuntu:
   `sudo apt update && sudo apt install -y dotnet-sdk-10.0 clang libsqlite3-dev`. If Ubuntu's
   feed has no .NET 10 package, Microsoft's install script works without `sudo`:
   `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0`.
2. Build in a clone inside the Linux file system, not in `/mnt/e`. A build in `/mnt/e` would
   overwrite the Windows build's `bin/` and `obj/` directories, and the Windows file system is slow
   from Linux. Clone the Windows repository with `git clone /mnt/e/Repos/Wylow/wylow ~/wylow`, and
   bring each new commit across with `git pull` in `~/wylow`.
3. Run `dotnet build`, then the suite with `--cc gcc`, with `--cc tsan`, and with `--interpret`.

## What it covers, and what it lacks

- On Linux, `NativeCompiler` knows the gcc toolchain, and the TSan toolchain through `clang++` or
  `g++`. Its `Clang`, `Msvc`, and `ClangSanitized` toolchains are clang-cl and MSVC, which exist
  only on Windows. A mode that builds with `clang++` on Linux would be a new toolchain, with its own
  flags and precompiled header.
- GNU ld on Linux prints its missing symbols, which the MSYS2 `ld` of the development machine does
  not. `interop/undefined_extern` passes in the gcc mode there.
- The `interop` tests pass with `--cc gcc`, and `examples/sqlite` prints the same rows as on
  Windows. The whole suite has not been run on Linux yet.
- A `// native` test that includes a Windows header, such as `interop/manifest_native`, guards it
  with `#ifdef _WIN32`, and the Linux run checks that guard.
