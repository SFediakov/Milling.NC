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
