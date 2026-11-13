# 0105. A manifest's `fetch` block names a native file to download, and the compiler caches the file by its hash

- Status: accepted (2026-11-12)
- Spec: `programs.manifest`, `interop.native`

## Decision

- A package's `native` block may hold one `fetch` block, and a platform block of `native` holds
  none. The `fetch` block gives `url`, `sha256`, and `files`:

  ```
  native {
      fetch {
          url = "https://sqlite.org/2026/sqlite-amalgamation-3540100.zip";
          sha256 = "0a9c8e34352dad46006aa82836d5383bff9dc6bc9ce5a60553c05aa30e6ad1f9";
          files = ["sqlite-amalgamation-3540100/sqlite3.c",
                   "sqlite-amalgamation-3540100/sqlite3.h"];
      }
  }
  ```

- `url` is an absolute URL whose scheme is `https`. `sha256` is the SHA-256 hash of the downloaded
  bytes, written as 64 lowercase hexadecimal digits. `files` lists the files of the download that
  the package uses, each by its path inside the archive. A path is one or more segments separated
  by `/`, and no segment is empty, `.`, or `..`.
- A URL whose path ends with `.zip`, `.tar`, `.tar.gz`, or `.tgz` names an archive. The compiler
  extracts the files that `files` names and keeps their paths. Any other URL names one file, and
  `files` gives the name of that file.
- The compiler downloads the file once, checks the hash, and keeps the file in a per-user cache
  outside the package. On Windows the cache is `%LOCALAPPDATA%\wyl\cache`. On other systems it is
  `$XDG_CACHE_HOME/wyl/cache`, or `~/.cache/wyl/cache` when `XDG_CACHE_HOME` is unset. The
  environment variable `WYL_CACHE_DIR` overrides the directory.
- An entry of the cache is `<cache>/sha256/<hash>/`. The downloaded bytes are in `archive`, and the
  extracted files are under `files/`, by their archive paths. Two packages that name one hash share
  one entry.
- The compiler checks the hash when it downloads the file. A later build trusts a cache entry whose
  files are present. When a file of the entry is missing, the compiler extracts it again from the
  kept archive.
- The compiler runs no code of the package.
- The fetched files join the package's native inputs. The directory of each fetched header is on
  the package's include path after `native/` and before the package's `include` directories. Each
  fetched `.c` or `.cpp` file is a native source of the package, and it compiles with the package's
  include directories and definitions.
- A build whose network is unavailable uses a cache entry when the build has one. When the build
  has no entry, the build reports the URL and the reason. No option forbids a download.
- `wyl fetch <file-or-dir>` loads the packages, fetches every file, reports the diagnostics, and
  exits without compiling. Every other command that loads a package fetches as it reads the
  package's native inputs.
- The errors are `WYL0550` (the downloaded file's hash differs), `WYL0551` (the archive holds no
  file of a path), `WYL0552` (the compiler cannot download the file), `WYL0553` (the hash is not 64
  lowercase hexadecimal digits), `WYL0554` (`files` is empty, or a path is not a relative path
  inside the archive), `WYL0555` (the URL does not use `https`), and `WYL0556` (the downloaded file
  is not a readable archive). A `fetch` block without `url`, `sha256`, or `files` is reported by
  the manifest's missing-entry error.

## Alternatives

- **The cache beside the compiler**, as the precompiled header is. A published compiler may sit in
  a read-only directory, and each installation of the compiler would download the same file again.
  A per-user cache matches Zig's global cache, Cargo's `$HOME/.cargo`, and Go's `GOPATH/pkg/mod`.
- **An option that forbids a download**, such as Cargo's `--offline` and Go's `GOPROXY=off`. A
  cache hit already avoids the network, and an option adds a rule that the spec does not need. The
  option can be added later without changing the manifest or the cache.
- **SHA3-256 as a second algorithm**, because sqlite.org publishes only SHA3-256 for the SQLite
  archive. Cargo, Go, and Bazel pin SHA-256, and the example's SHA-256 hash matches the published
  SHA3-256, so a reader can check it. A second algorithm adds a second way to write one rule.
- **Searching the archive for a file by its base name**, as the SQLite fetch scripts do. An exact
  path is unambiguous and the compiler can check it. A search silently picks one of two files of
  one name.
- **Running a script of the package**, as Cargo runs a `build.rs` for native files. Cargo's
  manifest has no field for a URL and a checksum (Cargo Book,
  [The manifest format](https://doc.rust-lang.org/cargo/reference/manifest.html): `build`), so a
  crate downloads native files in code that Cargo runs. Wylow's compiler reads the package's files
  and runs only the C++ toolchain, so a build of a program never runs code from a package.
- **Fetching only in `wyl build`.** `wyl check` and `wyl interpret` also search for an extern
  block's header while they lower the program (`WYL0545`), so a command that does not fetch would
  report a fetched header as missing. Fetching while the compiler reads the package's native inputs
  gives every command the same files, as Cargo downloads a crate for every command that needs it.
- **Extracting the whole archive.** The package uses the files that `files` names, and extracting
  only those files keeps unrelated files out of the package's include path.

## Why

- Each fetch script repeated download, hash, and extraction for one library. A manifest
  entry lets every package use one mechanism, and the compiler reports a wrong hash at the value
  that gives it.
- The hash is the identity of the downloaded content, so the cache can be content-addressed and
  shared between packages and programs.
- Zig checks a package's hash when it fetches the package and trusts the global cache afterwards
  ([Fetch.zig](https://github.com/ziglang/zig/blob/master/src/Package/Fetch.zig)). Checking the
  hash again on every build would read and hash the whole archive each time, and the only fault it
  would find is a corrupt cache entry, which deleting the entry repairs.
- HTTPS authenticates the transfer, and the hash pins the content against a change at the URL.
  Bazel requires `sha256` for the same reason: its documentation says that omitting the hash is a
  security risk because remote files can change
  ([http_archive](https://bazel.build/rules/lib/repo/http)).
- The compiler reads files and runs the C++ toolchain. It runs nothing that a package provides,
  which keeps a build of a program to the program's own code and the toolchain.
