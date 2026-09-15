# src - lessons learned and intended rule deviations

## Intended deviations from the root CLAUDE.md

- Version constant location. The root rule names `Server/Program.cs` and
  `WebAppVersion`. This repository has no server project. The constant is
  `AppVersion` in `src/Miller.App/Program.cs`, format `Build_Y.Z.X`, patch
  incremented on every app change. Test `tests/Miller.Tests/App/VersionFormatTests.cs`
  guards the format.
- One online reference exists on purpose: `scripts/vendor-packages.sh` names the
  public NuGet source for the one-time population of `third_party/nuget/`.
  Builds never use it; `NuGet.config` clears every source except the vendored
  folder. The Docker base image is the second external fetch and is pinned.
- `Avalonia.Diagnostics` is not referenced although `docs/ARCHITECTURE.md` 2.2 lists it at
  12.1.2. Avalonia 12 removed the package (its last version is 11.3.x); the replacement
  `AvaloniaUI.DiagnosticsSupport` is a different package outside the allowed list. It was
  Debug-only developer tooling with no shipped feature behind it, so it is dropped from
  `Directory.Packages.props`, `Miller.App.csproj` and `third_party/nuget/`. Adding
  `AvaloniaUI.DiagnosticsSupport` later is a one-line change in T-002 and T-006.

## Placeholder convention (architecture delivered without implementation)

- `.cs`, `.sh`, `Dockerfile`, `.editorconfig` placeholders are comment-only
  files under their final names. They compile as empty files once the project
  files exist.
- Formats that cannot hold a comment-only body (`.axaml`, `.csproj`, `.sln`,
  `.json`, `.props`, `.config`, `.ico`, `.png`, `.nc`, deliverable `.md`) are
  represented by `<final name>.placeholder.md`. The implementing task creates
  the real file and deletes the placeholder in the same commit. A leftover
  `.placeholder.md` next to a real file means the task was not finished.
- `MoveKind` lives in `Toolpath/ToolpathSegment.cs`, not in its own file, so
  that task T-031 stays within the three-file limit.

## Facts that are easy to get wrong

- `Milling_Heart_V2.STL` is binary although its header starts with `solid`.
  Format detection is by the size rule `84 + 50 * count == length`, never by
  the header text.
- Avalonia 12 headless testing depends on xunit v3 (`xunit.v3` 3.2.2 is the
  exact version `Avalonia.Headless.XUnit` 12.1.2 was built against). xunit 2
  packages must not be added.
- Windows renders through ANGLE (OpenGL ES); Linux through GLX. The only
  allowed difference is the shader version preamble chosen from `GlVersion`.
