# 0108. A native object is cached per user, by the compile's inputs

- Status: accepted (2026-11-13)
- Spec: `interop.native`

## Decision

- Each native source compiles into the per-user cache at
  `<cache>/native-objects/<key><extension>`. The key is the SHA-256 of the compiler's identity, the
  source's package's native lists, every file that the source can include
  (`PackageNative.IncludableFiles`), and the compiler's arguments except the output path.
- The cache is the directory that `WYL_CACHE_DIR` names, or `%LOCALAPPDATA%\wyl\cache` on Windows
  and `$XDG_CACHE_HOME/wyl/cache` or `~/.cache/wyl/cache` elsewhere. A download uses `sha256/` in
  the same directory (decision 0105).
- A build first checks the object in the program's `native-objects/` directory and its `.key` file.
  When the object is missing or its key differs, the build takes the object from the shared cache if
  the cache holds it, and otherwise compiles the source once into the cache. The program's directory
  then receives a copy named for the source: `{index}_{name}.obj` or `{index}_{name}.o`.
- A lock per key serializes concurrent compiles. A compile writes a temporary file, and the build
  moves the file into the cache only after the compiler succeeds.
- The compiler never deletes an entry of the shared cache. The program's `native-objects/`
  directory still drops the objects of sources that are removed or renamed.

## Alternatives

- **Link the cache object directly.** The linker's error names the object, and
  `BuildFailure.UserOf` maps an object's `{index}_{name}` back to its source by the index the
  program gave it. A cache file is named by its key and has no index, so the message would name the
  source only when its base name is unique. The copy keeps the current message.
- **Put the output path in the key.** That was the behavior before this decision: the arguments
  included `/Fo<program object>` or `-o <program object>`, so every program's key differed and no
  object was shared.
- **A cache beside each program.** That is the current per-program directory. It keeps objects for
  one program's rebuilds and does not share them with a second program.
- **A cache beside the compiler.** A published compiler may sit in a read-only directory, and each
  installation would compile the same sources again. Decision 0105 chose the per-user cache for
  downloads for the same reason.
- **Deleting old entries.** An entry is unused, not wrong. Eviction needs a size or age rule, and
  the object cache is small beside the downloads that share the directory. A user can delete the
  directory.

## Why

- The handoff measured a second program that depends on `sqlite` compiling the amalgamation again:
  4.8 seconds in a debug build and 21 seconds in a release build on the development machine. The
  source does not change between the programs, so the compile should not run again.
- The key already names everything a compile depends on, except the output path, which does not
  change the object. `BuildKeys.Identity` covers the toolchain, the mode, the compiler executable,
  and the runtime headers; `PackageNative.IncludableFiles` covers every file that the source can
  include.
- The program keeps its own copy because a linker's message and the classification of a failed link
  use the object's name. A copy of an object file costs little beside the compile.
- The cache is per user, like the downloads, because another user's compiler, environment, and trust
  boundary are not this user's.
