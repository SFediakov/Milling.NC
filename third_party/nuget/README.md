# Vendored NuGet packages

This folder is the only package source of the solution (`NuGet.config` clears every other source).
It holds `.nupkg` files for every package in `Directory.Packages.props`, their transitive dependencies,
and the .NET runtime and apphost packs for `win-x64` and `linux-x64` needed by self-contained publishing.

Populate it once, on a machine with internet access:

```bash
bash scripts/vendor-packages.sh
```

Verify offline afterwards (network disabled):

```bash
dotnet restore Miller.sln
```

Rules:

- Commit the `.nupkg` files. They are binary; `.gitattributes` marks them so.
- Never add a package without a task in `docs/DEVELOPMENT_GUIDE.md` and a matching `PackageVersion` entry.
- Never change a version here without changing `Directory.Packages.props` in the same commit.
- The Docker build copies this folder; the container has no online source.

What the script does beyond a plain restore:

- The restore runs with an empty `NetCoreTargetingPackRoot`, so `Microsoft.NETCore.App.Ref`, the apphost
  packs and the runtime packs are downloaded as packages instead of being resolved from the `packs`
  folder of the machine that runs the script. Without this the Docker build would miss the
  `win-x64` host pack, which the Windows SDK ships locally but Linux does not.
- The pack versions equal the runtime version bundled with the SDK that runs the script
  (`BundledNETCoreAppPackageVersion`). The Docker base image must contain the same SDK version,
  otherwise its publish step asks for runtime packs that are not vendored.
- `Avalonia.Diagnostics` is not vendored: Avalonia 12 removed the package (see `src/CLAUDE.md`).
