# 0106. A download and its extracted files have size limits, and a fetched path is a file name on every platform

- Status: accepted (2026-11-13)
- Spec: `programs.manifest`

## Decision

- The compiler refuses a downloaded file that is larger than 512 MiB (`WYL0552`).
- The compiler refuses to extract more than 512 MiB in total from one download (`WYL0556`).
- The compiler reads a compressed archive through a stream that stops after 1 GiB of expanded
  bytes, so an archive of unwanted entries cannot expand without bound (`WYL0556`).
- A path in `files` is one that Windows can write. A segment is not empty, `.`, or `..`,
  does not end with `.` or a space, and is not a name that Windows reserves for a device, such as
  `con` or `lpt1`. The path holds no backslash and no character that a Windows file name cannot
  hold: no control character, and none of `<`, `>`, `:`, `"`, `|`, `?`, or `*` (`WYL0554`).
- The compiler writes only the entries that `files` names. It checks each path before it extracts
  anything, and it does not write an entry that is a symbolic link.

## Alternatives

- **No limits.** The hash pins the bytes of the download, but it says nothing about their
  expansion. A small archive can expand to a file larger than the disk, and a small gzip stream can
  expand without bound while the compiler reads it. The package's C code runs when the program
  runs, but a build must not exhaust the machine before the program exists.
- **Limits in the manifest.** A manifest can raise its own limits, and the manifest is the thing
  that names the archive. A limit that the package sets is not a limit.
- **A user option that raises the limits.** A file above 512 MiB is rare for a native library, and
  an option adds a setting to every build. The limits can become an option if a package needs one.
- **Rejecting Windows device names only on Windows.** A manifest would work on Linux and fail on
  Windows, so a package would not be portable. The compiler rejects the names on every platform.
- **Extracting the whole archive into the cache.** The package uses only the files that `files`
  names, so a bomb in another entry would expand and fill the disk for no purpose.

## Why

- A zip or gzip bomb is a small file that expands to a much larger one. The download's hash
  protects the transfer, not the disk, so the compiler bounds what the archive can cost.
- 512 MiB is far larger than the amalgamation of SQLite, which is a few megabytes, and smaller
  than the free space of a build machine. The scan limit of 1 GiB leaves room for an archive whose
  entries the package does not use.
- A path that a Windows file system reads as a device, or a path whose trailing dot or space
  Windows strips, can write somewhere other than the file that the manifest names. A package that
  builds on one platform must build on the others.
- Writing only the named entries, and never a link, means that the archive cannot choose the
  cache's layout or reach a file outside it. A path with `..`, an absolute path, or a drive letter
  never becomes a file system path.
