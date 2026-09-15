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

## Lessons learned while executing the task list

- A namespace must not carry the name of a type it contains, and a project's
  root namespace must not equal a type name used next to it. `Miller.Application`
  hides `Avalonia.Application` inside `Miller.App`, so that base class is written
  `Avalonia.Application`. For the same reason the `HeightMap` folder uses the
  namespace `Miller.Core.HeightMaps` and the `Toolpath` folder uses
  `Miller.Core.Toolpaths` (tests mirror them as `Miller.Tests.Core.HeightMaps`
  and `Miller.Tests.Core.Toolpaths`). Placeholder headers were corrected.
- `SafeHeight` is a clearance above the stock top, not an absolute Z. The
  absolute rule made every default project invalid whenever machine zero sits at
  the stock bottom, which is the default origin mode; 16 validator tests failed
  on it before the change.
- The version bump applies to every task that changes Core, Application or App
  code, not only to files under `src/Miller.App`.
- Heights interpolated from triangles compare with a tolerance in tests; only
  assigned values (floor, NaN) compare exactly. Sampling a ridge at cell centers
  loses up to one cell size of height, so a fixture peak is asserted within the
  cell size, not within 0.01 mm.
- Gouge model: GougeChecker treats every cell as a flat plateau at its tip value and judges a
  sample by the cell it falls in. Strategies must be safe under that model: raster finishing
  crosses a cell boundary at the higher of the two tips, contour finishing traces the mask of
  allowed cells pulled into those cells, and a square with three allowed corners needs an elbow
  vertex or the chord cuts the forbidden cell. The linker joins passes only when the join feed is
  clear against the tip map.
- Rasterized heights carry float rounding (a 5.0 top reads 5.0000005). Tests compare interpolated
  values with a tolerance, and a mask at the exact stock top needs a small margin.
- The Bash tool truncates commands above roughly 8 KB, which cuts a heredoc mid-way and fails
  with an unmatched quote error. Write files larger than a few KB with the Write tool.
- The Linux launcher must not change the working directory before starting the binary;
  relative paths on the command line (project file, output file) would otherwise resolve
  inside dist/linux-x64. It starts the binary by its absolute path instead.
- InvariantGlobalization is on, so named cultures such as de-DE do not exist at run time.
  Tests that need a comma decimal separator clone the invariant culture and change its
  NumberFormat.
- The Bash tool also collapses a doubled backslash inside a heredoc into one, so a C# character
  literal for the backslash arrives broken. Files that need backslashes go through the Write tool,
  and path code uses Path.DirectorySeparatorChar instead of the literal.
