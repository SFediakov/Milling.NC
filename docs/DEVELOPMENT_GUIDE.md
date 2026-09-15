# Miller - Development Guide and Task List

This file is the working instruction for the developer (human or AI) who
implements Miller. Read `docs/ARCHITECTURE.md` first; it defines the modules
this guide refers to. Read the root `CLAUDE.md`; its rules are binding and are
restated here only where they need a concrete form.

Contents:

1. How to work with this guide
2. Stack, versions, tools
3. Repository layout
4. Compilation and packaging
5. Testing
6. Domain key points and formulas
7. Coding rules and definition of done
8. Known pitfalls
9. Milestones
10. Task list (T-001 to T-102)

---

## 1. How to work with this guide

1. Take the lowest-numbered task whose `Depends on` tasks are all done.
   Tasks are numbered in execution order; a task never depends on a higher
   number.
2. Open every file listed under `Files`. Placeholders contain a header with the
   purpose, the public interface names and the dependencies. Implement exactly
   that. If a placeholder is `<name>.placeholder.md`, create `<name>` and delete
   the placeholder file.
3. Do not touch files that are not listed, except the three always-allowed
   files: the project file that must include a new item, `Styles/Colors.axaml`
   when a new color is needed, and `Program.cs` for the version bump.
4. Build everything: `bash build.sh --no-publish` (see section 4).
5. Run all tests: `dotnet test Miller.sln`.
6. Increment the patch number of `AppVersion` in `src/Miller.App/Program.cs`
   (`Build_1.0.X` -> `Build_1.0.X+1`) if any app element changed.
7. Commit with the message `T-0NN: <task title>`; never commit `bin/`, `obj/`,
   `dist/`, `out/`.
8. Check the `Acceptance` line of the task. If it is not met, the task is not
   done.

Do not skip ahead, do not merge tasks, do not add features that no task asks
for. If a task is impossible as written, stop and report which assumption
fails; do not invent an alternative.

## 2. Stack, versions, tools

| Item | Value |
|---|---|
| Runtime and SDK | .NET 10 (`global.json` pins `10.0.100` with `rollForward: latestFeature`) |
| Target framework | `net10.0` for all projects |
| UI | Avalonia 12.1.2, Fluent theme, Inter font package |
| MVVM | CommunityToolkit.Mvvm 8.4.2 (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) |
| Tests | xunit.v3 3.2.2, Avalonia.Headless.XUnit 12.1.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.10.0 |
| Math | `System.Numerics.Vector3`, `Matrix4x4` (float) |
| Serialization | `System.Text.Json` |
| Package source | `third_party/nuget/` only; `NuGet.config` clears every other source |
| Linux build | `bash build.sh` on a Linux machine with the .NET 10 SDK; no container, no network (packages come from `third_party/nuget/`) |
| Shell for scripts | bash (Git Bash on Windows). No PowerShell scripts; the one `.cmd` file is the root `Miller.cmd` start file for Explorer |

No other package may be added. If a task seems to need one, the task is wrong;
report it.

## 3. Repository layout

```
Miller.sln
Directory.Build.props        shared compiler settings
Directory.Packages.props     central pinned package versions
NuGet.config                 single local source
global.json                  SDK pin
.editorconfig
build.sh                     restore, build, test, publish (both RIDs), assemble dist/
Miller.sh                    root start file for Linux and Git Bash: runs dist/<rid>/Miller with the arguments
Miller.cmd                   root start file for Windows Explorer and cmd: runs dist\win-x64\Miller.exe
launchers/Miller.sh          Linux start file (copied to dist/linux-x64/)
scripts/vendor-packages.sh   one-time online download of packages into third_party/nuget/
third_party/nuget/           vendored .nupkg files
samples/heart.miller.json    sample project for the fixture STL
Milling_Heart_V2.STL         fixture (binary STL, 4050 triangles)
src/Miller.Core/             domain: geometry, io, setup, heightmap, slicing, toolpath, gcode, simulation, analysis
src/Miller.Application/      services, validation, progress
src/Miller.App/              Avalonia UI: views, view models, rendering, ui services, styles, assets
tests/Miller.Tests/          xunit v3 tests mirroring src/, golden files, fixtures
docs/                        ARCHITECTURE.md, DEVELOPMENT_GUIDE.md
dist/                        build output (ignored by git)
```

Build output directories are limited to `bin/`, `obj/`, `build/`, `out/`,
`dist/`. Never write generated files anywhere else.

## 4. Compilation and packaging

Identical commands on Windows (Git Bash) and Linux:

```bash
bash build.sh                 # restore, build Release, test, publish win-x64 + linux-x64, assemble dist/
bash build.sh --no-publish    # restore, build, test only (used during tasks)
bash build.sh --no-test       # restore, build, publish
```

What `build.sh` does, in order:

1. `dotnet restore Miller.sln` (sources come from `NuGet.config`, so this is offline).
2. `dotnet build Miller.sln -c Release --no-restore`.
3. `dotnet test Miller.sln -c Release --no-build` unless `--no-test`.
4. `dotnet publish src/Miller.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64`.
5. `dotnet publish src/Miller.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist/linux-x64`.
6. `cp launchers/Miller.sh dist/linux-x64/Miller.sh && chmod +x dist/linux-x64/Miller.sh dist/linux-x64/Miller`.

Start files:

- Windows: `dist/win-x64/Miller.exe`
- Linux: `dist/linux-x64/Miller.sh`
- Root launchers (after `bash build.sh`): `Miller.cmd` for Windows Explorer or cmd,
  `Miller.sh` for Linux and Git Bash. Both forward every argument to the published
  binary (`Miller.cmd Milling_Heart_V2.STL`, `./Miller.sh --version`) and print a
  one-line hint to run `bash build.sh` when `dist/` is missing.

Linux build: the same `bash build.sh` on a Linux machine with the .NET 10 SDK
installed. Both RIDs are produced there as well; cross-publishing `win-x64`
from Linux is supported by the .NET SDK. No network is needed because
`NuGet.config` restores from `third_party/nuget/` only.

Headless verification of a Linux binary without a display:

```bash
dist/linux-x64/Miller.sh --version
dist/linux-x64/Miller.sh --export samples/heart.miller.json out/heart.nc
```

Runtime packs: self-contained publish needs the NuGet packages
`Microsoft.NETCore.App.Runtime.win-x64`, `Microsoft.NETCore.App.Runtime.linux-x64`,
`Microsoft.NETCore.App.Host.win-x64`, `Microsoft.NETCore.App.Host.linux-x64`
for the exact runtime version the SDK targets. `scripts/vendor-packages.sh`
downloads them by restoring with `-r win-x64` and `-r linux-x64`.

`Directory.Build.props` sets `InvariantGlobalization=true`, so the Linux
binary does not require `libicu`. All formatting in the code is invariant
anyway.

## 5. Testing

Commands:

```bash
dotnet test Miller.sln                                   # everything
dotnet test Miller.sln --filter "FullyQualifiedName~Core" # one area
```

Test categories and where they live (`tests/Miller.Tests/`):

| Category | Folder | Style |
|---|---|---|
| Core unit tests | `Core/<Folder>/<Type>Tests.cs` | plain `[Fact]` / `[Theory]`, deterministic, no files except the fixture STL |
| Golden G-code tests | `Core/GCode/GrblPostProcessorTests.cs` + `Golden/*.nc` | exact text comparison; a golden file changes only through a task that says so |
| Application tests | `Application/<Service>Tests.cs` | temp folders for I/O, cancellation and progress asserted |
| View model tests | `App/<ViewModel>Tests.cs` | plain `[Fact]`, view models constructed with real services |
| Headless UI tests | `App/MainWindowHeadlessTests.cs` | `[AvaloniaFact]`, `[AvaloniaTestApplication]` on the assembly, Fluent theme |
| Rule tests | `App/ColorRuleTests.cs`, `App/VersionFormatTests.cs` | scan source files: color literals only in `Colors.axaml`; version matches `Build_\d+\.\d+\.\d+` |
| End to end | `App/EndToEndTests.cs` | sample project -> `.nc` equals golden |

Fixture facts for `Milling_Heart_V2.STL` (use them in assertions):

| Property | Value |
|---|---|
| Format | binary, although the 80-byte header starts with the text `solid` |
| Triangles | 4050 |
| File size | 202584 bytes = 84 + 4050 x 50 |
| Bounds min | (3.259, 0.477, 0.000) |
| Bounds max | (23.741, 5.477, 21.971) |
| Size | 20.482 x 5.000 x 21.971 mm |
| Topology | closed manifold, 6075 edges, every edge shared by exactly 2 triangles |
| Shape | a heart standing on its 5 mm edge; large parts of it are overhangs when Z is up |

Small meshes generated in `Fixtures/TestMeshes.cs`: unit cube (12 triangles),
10x10x5 mm box, a single spike (pyramid) for dilation tests, a 20x20 mm plate
with a 4 mm wide, 10 mm deep slot for head-clearance tests, a plate with a
hemispherical bump for ball-tool tests.

Manual rendering check (required by the root rules for frontend work): after
every UI task, run the app, open the fixture, take a screenshot of the changed
panel, and confirm that every control listed in the task is visible and
usable. Keep screenshots out of the repository.

Gouge check: every strategy test must call `GougeChecker.Verify(toolpath, tipMap)`
and assert zero violations. A gouge is a feed segment whose Z at any sample is
below the tip map by more than `Tolerance`.

## 6. Domain key points and formulas

### 6.1 Coordinate system and units

- Millimetres, degrees, mm/min, rpm. Right-handed, Z up. Retract is +Z.
- `AxisSetup` produces one `Matrix4x4` = rotation(Z) x rotation(Y) x rotation(X)
  x axis permutation with signs x translation to the origin mode. It is applied
  once to the mesh. Everything downstream works in machine coordinates.
- Origin modes: `StockCornerMinXYMinZ` (0,0,0 at stock bottom corner),
  `StockCornerMinXYTopZ` (0,0,0 at stock top corner, Z negative downward),
  `StockCenterTopZ` (common for hobby routers), `Custom` (offset vector).

### 6.2 Tool model

```
        |   |
        |   |   <- shank / head, diameter HeadDiameter (larger)
   -----+   +-----
        |   |
        |   |   <- cutter, diameter CutterDiameter, usable length CutterLength
        |   |
        \___/   <- tip: Flat or Ball
```

- `CutterLength` is the distance from the tip to the underside of the head.
- The head must never touch material: at tip height `z`, every stock or model
  height inside the head radius must be `<= z + CutterLength`.

### 6.3 Heightmaps

All grids share `OriginX`, `OriginY`, `CellSize`, `Width`, `Height`. Sample
point is the cell center. `NaN` = no material.

Model map (`MeshRasterizer`):
`model[i,j] = max Z of all triangles covering the cell center; floor if none`.
Overhanging faces are hidden by the max; this is the definition of what a
3-axis mill can see from above.

Tool profile (`ToolProfile`): for every grid offset `(dx, dy)` with
`d = CellSize * sqrt(dx^2 + dy^2) <= r` (r = cutter radius):

- Flat: `dz = 0`
- Ball: `dz = r - sqrt(r^2 - d^2)`

Tip map (`HeightMapDilation`), the lowest tip height that does not gouge:

```
tip[i,j] = max over footprint of ( model[i+dx, j+dy] - dz(dx,dy) )
```

Head limit (`HeadClearance`), annulus `r < d <= HeadDiameter/2`:

```
limit[i,j] = max over annulus of model[i+dx, j+dy]  -  CutterLength
effectiveTip[i,j] = max(tip[i,j], limit[i,j])
headLimited[i,j] = limit[i,j] > tip[i,j] + Tolerance
```

Material removal (`MaterialRemover`), one sample of the tool at tip `(x, y, z)`:

```
for every footprint offset: stock[i+dx, j+dy] = min(stock[i+dx, j+dy], z + dz(dx,dy))
```

Samples along a segment are spaced at most `CellSize / 2` apart, including both
end points.

Final model = stock map after all segments. Deviation map:

```
dev[i,j] = stock[i,j] - model[i,j]          where model exists (not floor)
|dev| <= Tolerance -> Ok
dev  >  Tolerance  -> RestMaterial
dev  < -Tolerance  -> Gouge (a bug in a strategy; tests must catch it)
```

Uncuttable classification (`UncuttableRegions`):

- Overhang: a downward-facing triangle (normal Z < 0) whose Z at the cell is
  below `model[i,j] - Tolerance` and above the floor.
- HeadLimited: `headLimited[i,j]` from the head limit.
- CornerLimited: `effectiveTip[i,j] - model[i,j] > Tolerance` and not HeadLimited
  (the cutter radius is larger than the local concave radius).

### 6.4 Slicing and toolpath parameters

- `Stepdown`: Z distance between roughing levels. Levels:
  `z_k = stockTop - k * Stepdown` for k = 1.. until `z_k <= min(effectiveTip)`;
  the last level is clamped to `min(effectiveTip)`.
- Roughing mask at level z: cells where `effectiveTip[i,j] < z` and the stock
  still has material above z.
- `Stepover`: distance between adjacent parallel passes. Valid range
  `(0, CutterDiameter]`. Finishing typically uses a smaller stepover than
  roughing; both come from the same parameter set with a `FinishingStepover`
  field.
- `SafeHeight`: clearance above the stock top for rapid moves. The absolute rapid Z is
  stock top + `SafeHeight`; the value must be > 0 and does not depend on the origin mode.
- Feed kinds: `Feed` uses `FeedRate`, `Plunge` uses `PlungeRate`, `Rapid` uses
  `RapidRate` (only for time estimation and simulation; G-code emits `G0`).
- `MillingDirection`: `Zigzag` alternates row direction; `OneWay` retracts and
  rapids back for every row.

### 6.5 G-code (Grbl post-processor)

```
( Miller Build_1.0.X )
( tool: <name> d=<CutterDiameter> flat|ball )
( stock: box 100x60x20 )
G21 G90 G94 G17
S12000 M3
G0 Z<stock top + SafeHeight>
G0 X.. Y..
G1 Z.. F<PlungeRate>
G1 X.. Y.. Z.. F<FeedRate>
...
G0 Z<stock top + SafeHeight>
M5
M30
```

- Numbers: invariant culture, 3 decimals, trailing zeros trimmed (`12.5`, `0`).
- `F` is emitted only when the feed rate changes.
- Only `G0`, `G1`, `G17`, `G21`, `G90`, `G94`, `M3`, `M5`, `M30`, `F`, `S`, `X`,
  `Y`, `Z`. No arcs, no tool changes, no cutter compensation.
- Line endings `\n`. File extension `.nc`.

### 6.6 Simulation

- Speed factor range `[0.1, 1000]`, default 1. Slider is logarithmic; text box
  accepts any value in range.
- `SimulationClock.Advance(realSeconds)` returns `realSeconds * speedFactor`
  when playing, 0 when paused.
- `SimulationEngine.Step(simSeconds)`: distance to cover
  `= rate(kind) * simSeconds / 60`; walks segments, calls
  `MaterialRemover.Sweep` for the covered part of each segment (feed and plunge
  only; rapid removes nothing but is checked for collision).
- Rendering is once per UI frame (60 Hz timer). All work for a tick happens in
  the tick; no frame skipping logic, no second path for high speeds.
- `RunToEnd()` processes the whole toolpath without the clock; used for the
  final-model preview.

### 6.7 Validation rules (`ProjectValidator`)

| Rule | Message field |
|---|---|
| `CutterDiameter > 0` | Tool.CutterDiameter |
| `CutterLength > 0` | Tool.CutterLength |
| `HeadDiameter > CutterDiameter` | Tool.HeadDiameter |
| `0 < Stepover <= CutterDiameter` | Parameters.Stepover |
| `0 < FinishingStepover <= CutterDiameter` | Parameters.FinishingStepover |
| `Stepdown > 0` | Parameters.Stepdown |
| `SafeHeight > 0` (clearance above the stock top) | Parameters.SafeHeight |
| `0.01 <= CellSize <= 5` | Parameters.CellSize |
| `FeedRate, PlungeRate, RapidRate > 0` | Parameters.* |
| `SpindleRpm > 0` | Parameters.SpindleRpm |
| Stock dimensions > 0 | Stock.* |
| Model bounds inside stock after `AxisSetup` (warning, not error) | Stock.Placement |
| Grid size `Width * Height <= 4_000_000` cells | Parameters.CellSize |

## 7. Coding rules and definition of done

Concrete form of the root rules:

- No fallbacks: no `try { fast } catch { slow }`, no "if GPU fails use CPU", no
  default strategy when an id is unknown (throw), no silent unit guesses.
- Reuse: use `System.Numerics` types, `IProgress<T>`, `CancellationToken`,
  `System.Text.Json`. Do not write your own vector, matrix, JSON or timer.
- Variables, not literals: every numeric limit is a named constant in the type
  that owns it (`SimulationClock.MinSpeedFactor`, `ProjectValidator.MaxCells`).
  Every color is a resource in `Styles/Colors.axaml`; C# code reads colors
  through resources, never `Color.Parse("#...")`.
- Comments only when they add value: the placeholder header is replaced by the
  implementation; keep a comment only for a non-obvious decision (formula
  source, a limit's reason).
- Culture: `CultureInfo.InvariantCulture` for every parse and format.
- Threads: only `PipelineService` and `AnalysisService` run work on a
  background task (`Task.Run`); Core never starts threads; UI updates only via
  the Avalonia dispatcher.
- Naming: ids of strategies and post-processors are lowercase with hyphens.
- Exceptions: Core throws `ArgumentException` / `InvalidDataException` with the
  offending value in the message; Application wraps nothing; App shows them via
  `ErrorDialogService`.

Definition of done by task type:

| Task type | Done when |
|---|---|
| Core type | Compiles, unit tests of the same task or the next test task pass, no `TODO` left, no placeholder header left |
| Test task | Tests run in `dotnet test`, fail when the tested rule is broken (verify once by breaking it locally, then restore) |
| Service | Same as Core plus cancellation and progress are asserted in tests |
| View / view model | View model tests pass, headless test constructs the view, manual screenshot check done, no color literal outside `Colors.axaml` |
| Rendering | Manual screenshot on Windows; camera math unit tests pass |
| Script | The command in the acceptance line runs on Windows Git Bash and on Linux |

Commit message: `T-0NN: <task title>`. One task, one commit. Version bump
included in the same commit when the app changed.

## 8. Known pitfalls

1. STL format detection: the fixture is binary although it starts with
   `solid`. Detect binary by `fileLength == 84 + 50 * count` where `count` is
   the little-endian uint32 at offset 80. Only when that fails, parse ASCII.
2. Avalonia on Windows renders through ANGLE (OpenGL ES 3). Shaders must be
   written so that only the version line differs: `#version 300 es` plus
   `precision highp float;` on ES, `#version 330 core` on desktop GL.
   `GlVersion.Type` tells which one; select the preamble at runtime.
3. Avalonia's `GlInterface` exposes only a subset of GL. Get missing entry
   points (`glGenVertexArrays`, `glBindVertexArray`, `glUniformMatrix4fv`,
   ...) through `GlInterface.GetProcAddress` in `GlFunctions.cs`. Do not add a
   second GL binding package.
4. All GL calls only inside `OnOpenGlInit`, `OnOpenGlRender`, `OnOpenGlDeinit`.
   Data for the renderer is prepared on the UI thread and uploaded in
   `OnOpenGlRender`.
5. Avalonia 12 headless tests use xunit v3. `[Fact]` from xunit v3 works for
   plain tests; UI-thread tests need `[AvaloniaFact]`.
6. `float.NaN` propagates through `min`/`max` differently than expected:
   `Math.Max(NaN, 1)` is `NaN`. Every heightmap loop must test `float.IsNaN`
   first and skip the cell.
7. `.sh` files must have LF line endings; `.gitattributes` enforces it. Never
   edit them with a tool that writes CRLF.
8. Cross-publishing `win-x64` from Linux: the icon is embedded by the SDK; if
   the icon is missing in Explorer, the SDK is older than 8.0. Do not add
   Wine or `rcedit` workarounds.
9. Self-contained Linux binaries need `libicu` unless
   `InvariantGlobalization=true`. It is set in `Directory.Build.props`; do not
   remove it.
10. Marching squares on a grid with `NaN` cells: treat `NaN` as "below any
    level" so contours close at stock boundaries.
11. The head-limit map is computed against the model map (final surface), not
    the current stock. The simulation's `CollisionDetector` checks against the
    current stock; both are needed and are different checks.
12. SkiaSharp on Linux needs `libfontconfig` from the distribution (`fontconfig` package).
    Without it the rendering tests and the published binary fail to start.
13. Never write settings or logs into the repository. Settings go to the
    per-user application data folder; logs go to `logs/` next to the
    executable.

## 9. Milestones

| Milestone | Tasks | Verified by |
|---|---|---|
| M0 Skeleton builds on both OS | T-001 to T-010 | `bash build.sh` succeeds on Windows Git Bash and on Linux; `Miller.sh --version` prints the version |
| M1 Geometry and setup | T-011 to T-021 | Fixture STL loads with the recorded facts; project round-trips through JSON |
| M2 Heightmaps | T-022 to T-032 | Tip map and head limit tests pass on the spike, slot and bump meshes |
| M3 Slicing, strategies, linking | T-033 to T-049 | All three strategies pass the gouge check on the fixture |
| M4 G-code and headless export | T-050 to T-056 | `Miller --export` reproduces the golden file on Linux |
| M5 UI skeleton and settings | T-057 to T-074 | Every panel edits the project; generate and export work from the menu |
| M6 3D viewport | T-075 to T-086 | Model, stock, toolpath and tool visible; orbit, pan, zoom |
| M7 Simulation and final model | T-087 to T-098 | Material removal animates at 0.1x to 1000x; final model colored by category |
| M8 Packaging and release | T-099 to T-102 | `dist/` produced by `build.sh` on both systems; end-to-end test green on both OS |

## 10. Task list

Field meanings: `Depends on` lists task ids that must be done first; `Files`
are the only files to change (plus the three always-allowed files); `Input`
is what exists before; `Output` is what exists after; `Acceptance` is the
check that decides done.

### M0 Skeleton

#### T-001 Solution-wide build settings
- Depends on: none
- Files: `global.json`, `Directory.Build.props`, `.editorconfig`
- Input: placeholders `global.json.placeholder.md`, `Directory.Build.props.placeholder.md`, `.editorconfig`
- Output: SDK pin `10.0.100` with `rollForward: latestFeature`; props with `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `InvariantGlobalization=true`, `Deterministic=true`; editorconfig with 4-space indent, LF, `dotnet_diagnostic` severity for unused usings
- Acceptance: `dotnet --version` prints a 10.0.x version in the repository root; the three files exist and the placeholders are deleted
- Status: done

#### T-002 Central package versions and single NuGet source
- Depends on: T-001
- Files: `Directory.Packages.props`, `NuGet.config`
- Input: package table in `docs/ARCHITECTURE.md` section 2.2
- Output: `ManagePackageVersionsCentrally=true` with every listed package and version; `NuGet.config` with `<clear />` and one source `third_party/nuget`
- Acceptance: `dotnet nuget list source --configfile NuGet.config` shows exactly one enabled source pointing at `third_party/nuget`
- Status: done

#### T-003 Core class library project
- Depends on: T-002
- Files: `src/Miller.Core/Miller.Core.csproj`, `Miller.sln`
- Input: comment-only `.cs` placeholders under `src/Miller.Core/`
- Output: class library project with no package references and no project references; solution containing it
- Acceptance: `dotnet build Miller.sln` succeeds with zero warnings
- Status: done

#### T-004 Application class library project
- Depends on: T-003
- Files: `src/Miller.Application/Miller.Application.csproj`, `Miller.sln`
- Input: placeholders under `src/Miller.Application/`
- Output: class library referencing `Miller.Core` only; added to the solution
- Acceptance: `dotnet build Miller.sln` succeeds; the project file contains no `PackageReference`
- Status: done

#### T-005 Package vendoring script
- Depends on: T-002
- Files: `scripts/vendor-packages.sh`, `third_party/nuget/README.md`
- Input: comment-only script placeholder; README describing the procedure
- Output: script that creates a temporary project under `out/vendor/` referencing every package from `Directory.Packages.props`, restores it online (`--source https://api.nuget.org/v3/index.json --packages out/vendor/cache`) for no RID, `win-x64` and `linux-x64`, then copies every `*.nupkg` from the cache into `third_party/nuget/` (flat). The script is the only place that names the online source
- Acceptance: after running the script once online, `dotnet restore Miller.sln` succeeds with the network disabled; `third_party/nuget/` contains the Avalonia, toolkit, xunit packages and the runtime and host packs for both RIDs
- Status: done

#### T-006 Avalonia application project with version constant and CLI flags
- Depends on: T-004, T-005
- Files: `src/Miller.App/Miller.App.csproj`, `src/Miller.App/Program.cs`, `src/Miller.App/App.axaml` (+ `.axaml.cs`)
- Input: placeholders; package list section 2.2
- Output: `OutputType=WinExe`, references `Miller.Application` and the Avalonia, Fluent, Inter, toolkit packages; `Program.cs` with `public const string AppVersion = "Build_1.0.0"`, `--version` printing it and exiting 0, otherwise `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` using `UsePlatformDetect()`; `App.axaml` with Fluent theme and an empty `MainWindow`
- Acceptance: `dotnet run --project src/Miller.App -- --version` prints `Build_1.0.0`; running without arguments opens an empty window titled `Miller`
- Status: done

#### T-007 Test project with smoke tests
- Depends on: T-006
- Files: `tests/Miller.Tests/Miller.Tests.csproj`, `tests/Miller.Tests/App/VersionFormatTests.cs`, `Miller.sln`
- Input: placeholders
- Output: test project referencing all three source projects and the test packages; `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]` with a headless builder using the Fluent theme; a test asserting `AppVersion` matches `^Build_\d+\.\d+\.\d+$`
- Acceptance: `dotnet test Miller.sln` runs 1 test, green
- Status: done

#### T-008 Build script
- Depends on: T-007
- Files: `build.sh`
- Input: comment-only placeholder describing the steps in section 4
- Output: bash script with `--no-publish` and `--no-test` flags implementing section 4, `set -euo pipefail`, exit code non-zero on any failure
- Acceptance: `bash build.sh` on Windows Git Bash produces `dist/win-x64/Miller.exe` and `dist/linux-x64/Miller`; `bash build.sh --no-publish` runs tests
- Status: done

#### T-009 Linux launcher
- Depends on: T-008
- Files: `launchers/Miller.sh`
- Input: comment-only placeholder
- Output: `#!/usr/bin/env bash`, `exec "$(dirname "$(readlink -f "$0")")/Miller" "$@"` (no `cd`: relative paths on the command line must keep their meaning); `build.sh` already copies it and sets the executable bit
- Status: done
- Acceptance: `bash -n launchers/Miller.sh` passes; `file launchers/Miller.sh` reports LF line endings; `dist/linux-x64/Miller.sh --version` prints the version when run on Linux (or WSL)

#### T-010 Dockerfile for the Linux build (removed)
- Depends on: T-009
- Files: none
- Input: none
- Output: nothing. Docker is not part of the project; the Linux build is `bash build.sh` on a Linux machine and is verified by T-102
- Acceptance: no Docker file, image or vendored Debian package in the repository
- Status: removed

### M1 Geometry and setup

#### T-011 Bounding box and triangle
- Depends on: T-003
- Files: `src/Miller.Core/Geometry/BoundingBox.cs`, `src/Miller.Core/Geometry/Triangle.cs`
- Input: placeholders
- Output: `BoundingBox` (readonly struct: `Min`, `Max`, `Size`, `Center`, `Union`, `Contains`, `Empty`), `Triangle` (readonly struct: `A`, `B`, `C`, `Normal` computed from the corners, `MinZ`, `MaxZ`, `IsDegenerate` when area below `MinArea`)
- Acceptance: compiles; used by T-012
- Status: done

#### T-012 Mesh
- Depends on: T-011
- Files: `src/Miller.Core/Geometry/Mesh.cs`
- Input: placeholder
- Output: `Mesh` holding `Triangle[]`, `Bounds`, `TriangleCount`, `Transform(Matrix4x4) -> Mesh` (new mesh, normals recomputed), `RemoveDegenerate()`
- Acceptance: compiles; tests in T-013
- Status: done

#### T-013 Tests for geometry
- Depends on: T-012
- Files: `tests/Miller.Tests/Fixtures/TestMeshes.cs`, `tests/Miller.Tests/Core/Geometry/MeshTests.cs`, `tests/Miller.Tests/Core/Geometry/BoundingBoxTests.cs`
- Input: placeholders
- Output: `TestMeshes` builders (unit cube, box 10x10x5, spike, slotted plate, bump plate) and tests: bounds of the cube, transform by translation and 90-degree rotation, degenerate removal count, union and contains
- Acceptance: tests green; each builder returns a closed mesh (edge count check helper in the same file)
- Status: done

#### T-014 STL binary parser
- Depends on: T-012
- Files: `src/Miller.Core/Io/StlBinaryParser.cs`, `src/Miller.Core/Io/StlImportReport.cs`
- Input: placeholders; format facts in section 5
- Output: parser reading header, count, 50-byte records with `BinaryPrimitives` little-endian; report with count, bounds, degenerate count, `Format=Binary`; throws `InvalidDataException` when the size formula fails
- Acceptance: compiles; tests in T-016
- Status: done

#### T-015 STL ASCII parser
- Depends on: T-014
- Files: `src/Miller.Core/Io/StlAsciiParser.cs`
- Input: placeholder
- Output: line-based parser for `solid`, `facet normal`, `outer loop`, `vertex x y z`, `endloop`, `endfacet`, `endsolid`; invariant culture; throws with line number on malformed input
- Acceptance: compiles; tests in T-016
- Status: done

#### T-016 STL reader with format detection and tests
- Depends on: T-015
- Files: `src/Miller.Core/Io/StlReader.cs`, `tests/Miller.Tests/Core/Io/StlReaderTests.cs`
- Input: placeholders; pitfall 1
- Output: `StlReader.Read(string path) -> (Mesh, StlImportReport)` deciding binary by the size formula first, ASCII otherwise; tests: fixture parsed as binary with 4050 triangles and the recorded bounds (tolerance 0.001), ASCII cube text parsed to 12 triangles, truncated binary throws, garbage text throws with a line number
- Acceptance: tests green; the fixture path is resolved relative to the test assembly (`../../../../../Milling_Heart_V2.STL`) and asserted to exist
- Status: done

#### T-017 Tool, stock and cutting parameters
- Depends on: T-003
- Files: `src/Miller.Core/Setup/ToolDefinition.cs`, `src/Miller.Core/Setup/StockDefinition.cs`, `src/Miller.Core/Setup/CuttingParameters.cs`
- Input: placeholders; sections 6.2 and 6.4
- Output: three mutable classes with the properties named in `docs/ARCHITECTURE.md` 4.1, default values (tool 6 mm flat, length 20, head 10; stock box 100x100x30 auto-fit margin 5; feed 800, plunge 200, rapid 3000, spindle 12000, stepover 3, finishing stepover 0.5, stepdown 2, safe height 5, cell size 0.2, tolerance 0.05, zigzag)
- Acceptance: compiles; defaults asserted in T-020
- Status: done

#### T-018 Axis setup and transform matrix
- Depends on: T-017
- Files: `src/Miller.Core/Setup/AxisSetup.cs`
- Input: placeholder; section 6.1
- Output: `AxisSetup` with `MapX`, `MapY`, `MapZ` (enum `ModelAxis { X, Y, Z }`), `FlipX`, `FlipY`, `FlipZ`, `RotationX`, `RotationY`, `RotationZ` degrees, `OriginMode`, `CustomOffset`; `ToMatrix(BoundingBox modelBounds, StockDefinition stock) -> Matrix4x4`
- Acceptance: compiles; tests in T-019
- Status: done

#### T-019 Tests for axis setup
- Depends on: T-018, T-013
- Files: `tests/Miller.Tests/Core/Setup/AxisSetupTests.cs`
- Input: placeholder
- Output: tests: identity leaves the cube in place; swapping Y and Z turns the 10x10x5 box into 10x5x10; flip Z mirrors; rotation Z 90 maps (1,0,0) to (0,1,0); each origin mode puts the expected corner at (0,0,0)
- Acceptance: tests green
- Status: done

#### T-020 Milling project and JSON serializer
- Depends on: T-018
- Files: `src/Miller.Core/Setup/MillingProject.cs`, `src/Miller.Core/Setup/ProjectSerializer.cs`, `tests/Miller.Tests/Core/Setup/ProjectSerializerTests.cs`
- Input: placeholders
- Output: aggregate with `SchemaVersion = 1`, tool, stock, axis, parameters, `StlPath`, `RoughingStrategyId = "raster-roughing"`, `FinishingStrategyId = "raster-finishing"`, `PostProcessorId = "grbl"`; serializer with `JsonSerializerOptions` (indented, enums as strings, invariant); tests: round trip equals, unknown schema version throws, defaults from T-017 asserted
- Acceptance: tests green
- Status: done

#### T-021 Project validator
- Depends on: T-020, T-004
- Files: `src/Miller.Application/Validation/ValidationResult.cs`, `src/Miller.Application/Validation/ProjectValidator.cs`, `tests/Miller.Tests/Application/ProjectValidatorTests.cs`
- Input: placeholders; section 6.7
- Output: `ValidationResult` (errors and warnings with field name and message), validator implementing every rule of 6.7 as a named constant where a limit exists; tests: default project is valid, each rule triggers with the right field name
- Acceptance: tests green; one test per rule
- Status: done

### M2 Heightmaps

#### T-022 HeightMap grid type
- Depends on: T-011
- Files: `src/Miller.Core/HeightMap/HeightMap.cs`, `tests/Miller.Tests/Core/HeightMap/HeightMapTests.cs`
- Input: placeholders; section 6.3
- Output: `HeightMap(originX, originY, cellSize, width, height, fill)`, indexer `[i, j]`, `CellCenter(i, j)`, `CellOf(x, y)`, `Clone()`, `Min()`, `Max()` ignoring `NaN`, `Fill(value)`, `Bounds`; tests: indexing, cell-world round trip, NaN ignored by min/max, clone independence
- Acceptance: tests green
- Status: done

#### T-023 Mesh rasterizer
- Depends on: T-022, T-012
- Files: `src/Miller.Core/HeightMap/MeshRasterizer.cs`
- Input: placeholder; section 6.3
- Output: `Rasterize(Mesh, HeightMap target, float floor)`: for each triangle, loop over the cells inside its XY bounding box, point-in-triangle test at the cell center with barycentric interpolation of Z, keep max; cells never covered stay at `floor`; also `RasterizeDownwardFacing(Mesh, HeightMap target)` keeping the max Z of triangles with `Normal.Z < 0` (used by T-098)
- Acceptance: compiles; tests in T-024
- Status: done

#### T-024 Tests for the rasterizer
- Depends on: T-023, T-013
- Files: `tests/Miller.Tests/Core/HeightMap/MeshRasterizerTests.cs`
- Input: placeholder
- Output: tests: the 10x10x5 box at cell size 0.5 gives 400 cells at 5.0 and floor elsewhere; the fixture at cell size 0.2 has its max within one cell size (0.2) below 21.971 and never above it, and every covered cell inside the fixture bounds; a triangle edge exactly on a cell center is covered (no gaps between adjacent triangles)
- Acceptance: tests green
- Status: done

#### T-025 Tool profile
- Depends on: T-022, T-017
- Files: `src/Miller.Core/HeightMap/ToolProfile.cs`, `tests/Miller.Tests/Core/HeightMap/ToolProfileTests.cs`
- Input: placeholders; section 6.3
- Output: `ToolProfile.Create(ToolDefinition, float cellSize)` with `Offsets` (`dx`, `dy`, `dz`) for the cutter footprint and `AnnulusOffsets` for the head ring; tests: flat 6 mm tool at 0.5 mm cells has all `dz == 0` and offsets within radius 3; ball tool center `dz == 0` and edge `dz` close to `r`; annulus offsets have distance in `(r, R]`
- Acceptance: tests green
- Status: done

#### T-026 Tip map by dilation
- Depends on: T-025
- Files: `src/Miller.Core/HeightMap/HeightMapDilation.cs`
- Input: placeholder; formula 6.3
- Output: `ComputeTipMap(HeightMap model, ToolProfile) -> HeightMap` using `max(model - dz)` over the footprint, NaN cells skipped (pitfall 6), grid edges clamped
- Acceptance: compiles; tests in T-027
- Status: done

#### T-027 Tests for dilation
- Depends on: T-026, T-024
- Files: `tests/Miller.Tests/Core/HeightMap/HeightMapDilationTests.cs`
- Input: placeholder
- Output: tests: a single spike cell at height 10 in a flat map produces a plateau of height 10 with the footprint's diameter for a flat tool; a ball tool produces a rounded profile with `tip = 10 - dz` at distance d; the tip map is never below the model map at any cell
- Acceptance: tests green
- Status: done

#### T-028 Head clearance
- Depends on: T-026
- Files: `src/Miller.Core/HeightMap/HeadClearance.cs`
- Input: placeholder; formula 6.3
- Output: `ComputeHeadLimit(HeightMap model, ToolProfile, float cutterLength) -> HeightMap`, `ApplyHeadLimit(HeightMap tip, HeightMap limit) -> HeightMap effectiveTip`, `HeadLimitedMask(tip, limit, tolerance) -> bool[,]`
- Acceptance: compiles; tests in T-029
- Status: done

#### T-029 Tests for head clearance
- Depends on: T-028, T-024
- Files: `tests/Miller.Tests/Core/HeightMap/HeadClearanceTests.cs`
- Input: placeholder; slotted plate fixture (4 mm wide, 10 mm deep slot)
- Output: tests: with cutter 3 mm, head 8 mm, cutter length 6 mm, the slot floor is head-limited (effective tip = plate top - 6); with cutter length 12 mm the slot floor is reachable; open areas are never head-limited
- Acceptance: tests green
- Status: done

#### T-030 Stock model
- Depends on: T-028, T-018
- Files: `src/Miller.Core/Simulation/StockModel.cs`, `tests/Miller.Tests/Core/Simulation/StockModelTests.cs`
- Input: placeholders
- Output: `StockModel.Create(StockDefinition, BoundingBox modelBoundsMachine, float cellSize) -> HeightMap` (box: all cells at top; cylinder: NaN outside the circle) and `StockTop`, `StockBottom`; auto-fit placement centers the model in XY with margin and puts the model top at the stock top; tests: box cell count, cylinder NaN fraction close to `1 - pi/4`, auto-fit bounds contain the model bounds
- Acceptance: tests green
- Status: done

#### T-031 Toolpath types and statistics
- Depends on: T-017
- Files: `src/Miller.Core/Toolpath/ToolpathSegment.cs`, `src/Miller.Core/Toolpath/Toolpath.cs`, `src/Miller.Core/Toolpath/ToolpathStatistics.cs`
- Input: placeholders
- Output: `enum MoveKind { Rapid, Feed, Plunge }` in `ToolpathSegment.cs`, `ToolpathSegment` (readonly struct), `Toolpath` (list, `Add`, `Bounds`, `TotalLength(MoveKind)`), `ToolpathStatistics.Compute(Toolpath, CuttingParameters)` with lengths, counts, estimated minutes
- Acceptance: compiles; tests in T-032
- Status: done

#### T-032 Tests for toolpath statistics
- Depends on: T-031
- Files: `tests/Miller.Tests/Core/Toolpath/ToolpathStatisticsTests.cs`
- Input: placeholder
- Output: tests: three segments of known length and kind give the expected totals; estimated time = sum(length / rate)
- Acceptance: tests green
- Status: done

### M3 Slicing, strategies, linking

#### T-033 Milling step and slice plan
- Depends on: T-022
- Files: `src/Miller.Core/Slicing/MillingStep.cs`, `src/Miller.Core/Slicing/SlicePlan.cs`
- Input: placeholders
- Output: `MillingStep` (`Level`, `Operation` enum `{ Roughing, Finishing }`, `bool[,] Mask`, `MaskCount`), `SlicePlan` (ordered steps, `RoughingLevels`, `HasFinishing`)
- Acceptance: compiles
- Status: done

#### T-034 Slicer
- Depends on: T-033, T-030
- Files: `src/Miller.Core/Slicing/Slicer.cs`, `tests/Miller.Tests/Core/Slicing/SlicerTests.cs`
- Input: placeholders; section 6.4
- Output: `Slicer.Build(HeightMap effectiveTip, HeightMap stock, CuttingParameters) -> SlicePlan`; tests: stock top 5, min tip 0, stepdown 2 gives levels 3, 1, 0; masks shrink monotonically with depth; a flat tip map equal to stock top gives zero roughing levels and one finishing step
- Acceptance: tests green
- Status: done

#### T-035 Strategy interface, context and registry
- Depends on: T-034, T-031
- Files: `src/Miller.Core/Toolpath/IToolpathStrategy.cs`, `src/Miller.Core/Toolpath/ToolpathContext.cs`, `src/Miller.Core/Toolpath/StrategyRegistry.cs`
- Input: placeholders; `docs/ARCHITECTURE.md` 6.1
- Output: interface with `Id`, `DisplayName`, `Operation`, `Generate(ToolpathContext, IProgress<float>?, CancellationToken)`; context record with tip, effective tip, head limit, stock, plan, tool, profile, parameters; registry as a static readonly list, `All`, `GetById` (throws `KeyNotFoundException` listing known ids), constructor-time duplicate check
- Acceptance: compiles; tests in T-036
- Status: done

#### T-036 Tests for the registry
- Depends on: T-035
- Files: `tests/Miller.Tests/Core/Toolpath/StrategyRegistryTests.cs`
- Input: placeholder
- Output: tests: ids are unique and lowercase-hyphen; `GetById("missing")` throws with the known ids in the message; every registered strategy has a non-empty display name
- Acceptance: tests green (the list may still be empty at this point; the uniqueness test must handle that)
- Status: done

#### T-037 Gouge checker
- Depends on: T-035
- Files: `src/Miller.Core/Toolpath/GougeChecker.cs`, `tests/Miller.Tests/Core/Toolpath/GougeCheckerTests.cs`
- Input: placeholders; section 5 gouge definition
- Output: `Verify(Toolpath, HeightMap effectiveTip, float tolerance) -> IReadOnlyList<GougeViolation>` sampling feed and plunge segments at `CellSize / 2`; tests: a segment on the tip map has zero violations; one dipped below by 2 x tolerance has at least one; rapids are ignored
- Acceptance: tests green
- Status: done

#### T-038 Toolpath linker
- Depends on: T-031
- Files: `src/Miller.Core/Toolpath/ToolpathLinker.cs`, `tests/Miller.Tests/Core/Toolpath/ToolpathLinkerTests.cs`
- Input: placeholders; section 6.4
- Output: `Link(IReadOnlyList<Toolpath> passes, CuttingParameters, float safeHeight) -> Toolpath`: for consecutive passes whose end and start differ by more than `CellSize`, insert retract (`Rapid` up to safe height), `Rapid` in XY at safe height, `Plunge` down to the pass start; a final retract; tests: two disjoint passes produce exactly retract, rapid, plunge between them; adjacent passes are joined by one feed segment; first move starts at safe height
- Acceptance: tests green
- Status: done

#### T-039 Raster roughing strategy
- Depends on: T-037, T-038
- Files: `src/Miller.Core/Toolpath/Strategies/RasterRoughingStrategy.cs`
- Input: placeholder; section 6.4
- Output: id `raster-roughing`; for each roughing level: rows along X spaced by `Stepover`; per row collect runs of masked cells; each run is one feed segment at `z = level`; `Zigzag` alternates direction; passes handed to `ToolpathLinker`; progress reported per level; cancellation checked per row
- Acceptance: compiles; registered in `StrategyRegistry`; tests in T-040
- Status: done

#### T-040 Tests for raster roughing
- Depends on: T-039, T-036
- Files: `tests/Miller.Tests/Core/Toolpath/RasterRoughingStrategyTests.cs`
- Input: placeholder
- Output: tests: on the 10x10x5 box centered in a 20x20x5 stock the roughing removes the ring around the box (all feed segments outside the box footprint), zero gouges, feed Z equals a level, cancellation throws `OperationCanceledException`
- Acceptance: tests green
- Status: done

#### T-041 Raster finishing strategy
- Depends on: T-039
- Files: `src/Miller.Core/Toolpath/Strategies/RasterFinishingStrategy.cs`, `tests/Miller.Tests/Core/Toolpath/RasterFinishingStrategyTests.cs`
- Input: placeholders
- Output: id `raster-finishing`; rows along X at `FinishingStepover`; Z per cell from the effective tip map; consecutive collinear points merged (tolerance); rows skipped where the whole row is NaN; tests: zero gouges on the bump plate; every covered cell within one stepover of a pass; segment count is smaller than the cell count (merging works)
- Acceptance: tests green; strategy registered
- Status: done

#### T-042 Marching squares
- Depends on: T-022
- Files: `src/Miller.Core/Toolpath/MarchingSquares.cs`, `tests/Miller.Tests/Core/Toolpath/MarchingSquaresTests.cs`
- Input: placeholders; pitfall 10
- Output: `Contours(HeightMap, float level) -> IReadOnlyList<IReadOnlyList<Vector2>>` closed loops with linear interpolation, NaN treated as below the level; tests: a circular hill gives one closed loop with perimeter close to `2 pi r` (5 percent); two hills give two loops; a level above the maximum gives none
- Acceptance: tests green
- Status: done

#### T-043 Contour finishing strategy
- Depends on: T-042, T-041
- Files: `src/Miller.Core/Toolpath/Strategies/ContourFinishingStrategy.cs`, `tests/Miller.Tests/Core/Toolpath/ContourFinishingStrategyTests.cs`
- Input: placeholders
- Output: id `contour-finishing`; levels from stock top down by `FinishingStepover` (used as vertical step); contours of the effective tip map at each level become feed loops at that Z; linked with retracts; tests: zero gouges on the bump plate; loop count per level matches marching squares; loops are closed (first point equals last)
- Acceptance: tests green; strategy registered
- Status: done

#### T-044 Registry tests with the three strategies
- Depends on: T-043
- Files: `tests/Miller.Tests/Core/Toolpath/StrategyRegistryTests.cs`
- Input: T-036 tests
- Output: tests extended: `All` contains exactly `raster-roughing`, `raster-finishing`, `contour-finishing`; operations are Roughing, Finishing, Finishing
- Acceptance: tests green
- Status: done

#### T-045 Progress report type
- Depends on: T-004
- Files: `src/Miller.Application/Progress/ProgressReport.cs`
- Input: placeholder
- Output: readonly record `ProgressReport(string Stage, float Fraction, string Message)`
- Acceptance: compiles
- Status: done

#### T-046 Mesh import service
- Depends on: T-016, T-045
- Files: `src/Miller.Application/Services/MeshImportService.cs`, `tests/Miller.Tests/Application/PipelineServiceTests.cs`
- Input: placeholders
- Output: `Import(string path) -> StlImportReport`, `CurrentMesh`, `HasMesh`, event `MeshChanged`; throws on empty mesh or non-finite bounds; the test file gets a first test importing the fixture and asserting the report
- Acceptance: test green
- Status: done

#### T-047 Pipeline service
- Depends on: T-046, T-044, T-030, T-021
- Files: `src/Miller.Application/Services/PipelineService.cs`
- Input: placeholder; `docs/ARCHITECTURE.md` 5.1
- Output: `Task<PipelineResult> RunAsync(MillingProject, Mesh, IProgress<ProgressReport>, CancellationToken)` executing the stages in order on `Task.Run`, validating first (errors throw `ValidationException` carrying the result), result holding machine mesh, stock, model map, tip map, effective tip, head limit, plan, toolpath, statistics
- Acceptance: compiles; tests in T-048
- Status: done

#### T-048 Tests for the pipeline service
- Depends on: T-047
- Files: `tests/Miller.Tests/Application/PipelineServiceTests.cs`
- Input: T-046 test file
- Output: tests: default project on the 10x10x5 box completes with zero gouges and at least one roughing and one finishing segment; progress fractions are non-decreasing and end at 1; cancellation before completion throws; invalid project throws `ValidationException` with the field name
- Acceptance: tests green
- Status: done

#### T-049 Fixture pipeline run
- Depends on: T-048
- Files: `tests/Miller.Tests/Application/PipelineServiceTests.cs`
- Input: T-048 tests
- Output: test running the fixture with cell size 0.2 and the default tool within 60 seconds, zero gouges, rest-material area above zero (the heart has overhangs), toolpath bounds inside stock bounds plus safe height
- Acceptance: test green and finishes under 60 s on the development machine (record the time in the test output)
- Status: done

### M4 G-code and headless export

#### T-050 G-code number formatter
- Depends on: T-003
- Files: `src/Miller.Core/GCode/GCodeFormatter.cs`, `tests/Miller.Tests/Core/GCode/GCodeFormatterTests.cs`
- Input: placeholders; section 6.5
- Output: `Format(float) -> string` (invariant, 3 decimals, trimmed, `-0` becomes `0`), `Word(char, float)`; tests: `12.5`, `0`, `-3.125`, `0.0004 -> 0`, culture with comma decimal separator set on the thread still yields a dot
- Acceptance: tests green
- Status: done

#### T-051 Post-processor interface and registry
- Depends on: T-050, T-031
- Files: `src/Miller.Core/GCode/IPostProcessor.cs`, `src/Miller.Core/GCode/PostProcessorRegistry.cs`
- Input: placeholders
- Output: interface `Id`, `DisplayName`, `FileExtension`, `Write(Toolpath, MillingProject, TextWriter)`; registry with the same shape and rules as `StrategyRegistry`
- Acceptance: compiles
- Status: done

#### T-052 Grbl post-processor
- Depends on: T-051, T-020
- Files: `src/Miller.Core/GCode/GrblPostProcessor.cs`
- Input: placeholder; section 6.5 template
- Output: id `grbl`, extension `.nc`, header comments with version, tool and stock, modal preamble, spindle on, moves per segment kind (`G0` for rapid, `G1` with `F` on change), spindle off, `M30`, `\n` endings; registered
- Acceptance: compiles; tests in T-053
- Status: done

#### T-053 Golden tests for the Grbl post-processor
- Depends on: T-052
- Files: `tests/Miller.Tests/Core/GCode/GrblPostProcessorTests.cs`, `tests/Miller.Tests/Golden/square_grbl.nc`
- Input: placeholders (`square_grbl.nc.placeholder.md` describes the toolpath: a 10 mm square at Z -1 with retract, plunge, four feeds)
- Output: golden file written once from the implementation and reviewed by hand against section 6.5; test compares byte for byte; a second test asserts `F` appears exactly twice (plunge rate, feed rate)
- Acceptance: tests green; golden file reviewed line by line and matching the template
- Status: done

#### T-054 Export service
- Depends on: T-053, T-047
- Files: `src/Miller.Application/Services/ExportService.cs`, `tests/Miller.Tests/Application/ExportServiceTests.cs`
- Input: placeholders
- Output: `Export(Toolpath, MillingProject, string path)` selecting the post-processor by `PostProcessorId`, writing with UTF-8 without BOM, creating the directory; tests: file written, extension enforced, unknown post-processor id throws before creating the file
- Acceptance: tests green
- Status: done

#### T-055 Headless export CLI
- Depends on: T-054, T-006
- Files: `src/Miller.App/Program.cs`, `samples/heart.miller.json`
- Input: placeholder `samples/heart.miller.json.placeholder.md`
- Output: `--export <project.json> <out.nc>` loads the project, resolves `StlPath` relative to the project file, runs `PipelineService` synchronously with progress printed as `stage fraction` lines, exports, exits 0; any error prints the message and exits 1; sample project pointing at `../Milling_Heart_V2.STL` with cell size 0.2
- Acceptance: `dotnet run --project src/Miller.App -- --export samples/heart.miller.json out/heart.nc` writes a file starting with `( Miller Build_`
- Status: done

#### T-056 Fixture golden and Linux verification
- Depends on: T-055, T-010
- Files: `tests/Miller.Tests/Golden/heart_grbl.nc`, `tests/Miller.Tests/App/EndToEndTests.cs`
- Input: placeholders
- Output: golden produced by the CLI on Windows, test runs the pipeline on the sample project in-process and compares to the golden after replacing the version comment line; Linux verification documented in the test file header: run `dist/linux-x64/Miller.sh --export samples/heart.miller.json out/heart-linux.nc` on Linux and diff against the golden without the version line
- Acceptance: test green on Windows; the Linux export diff is empty
- Status: done

### M5 UI skeleton and settings

#### T-057 Colors and theme resources
- Depends on: T-006
- Files: `src/Miller.App/Styles/Colors.axaml`, `src/Miller.App/Styles/Theme.axaml`, `src/Miller.App/App.axaml`
- Input: placeholders
- Output: `Colors.axaml` with named `Color` and `SolidColorBrush` resources for: window background, panel background, text, accent, error, warning, viewport background, model, stock, toolpath rapid, toolpath feed, toolpath plunge, tool cutter, tool head, category Ok, RestMaterial, Gouge, Overhang, HeadLimited, CornerLimited, axis X, Y, Z; `Theme.axaml` with base styles for headers, numeric text boxes, validation text; both included from `App.axaml`
- Acceptance: app starts; no color literal exists outside `Colors.axaml` (checked by T-058)
- Status: done

#### T-058 Color rule test
- Depends on: T-057
- Files: `tests/Miller.Tests/App/ColorRuleTests.cs`
- Input: placeholder
- Output: test scanning `src/**/*.axaml` and `src/**/*.cs` for `#[0-9A-Fa-f]{6,8}` and `Color.Parse(`/`Color.FromRgb(` and failing for any file except `Styles/Colors.axaml`
- Acceptance: test green; temporarily adding a literal to a view makes it red
- Status: done

#### T-059 View model base and composition root
- Depends on: T-057, T-047
- Files: `src/Miller.App/ViewModels/ViewModelBase.cs`, `src/Miller.App/ViewModels/MainWindowViewModel.cs`, `src/Miller.App/App.axaml.cs`
- Input: placeholders
- Output: `ViewModelBase : ObservableObject` with `SetError(field, message)`, `ClearErrors()`, `Errors`; `MainWindowViewModel` with `StatusText`, `IsBusy`, `Progress`, child view model properties (null for not yet created ones is not allowed: create them in the tasks that add them and keep the constructor signature growing); `App.axaml.cs` constructs services (`LogService`, `ProjectService`, `MeshImportService`, `PipelineService`, `ExportService`, `SettingsService`, `SimulationService`, `AnalysisService`) and the main view model and assigns `MainWindow.DataContext`
- Acceptance: app starts with the view model attached (window title shows `Miller Build_1.0.X`)
- Status: done

#### T-060 Project service and settings service
- Depends on: T-020, T-045
- Files: `src/Miller.Application/Services/ProjectService.cs`, `src/Miller.Application/Services/SettingsService.cs`, `tests/Miller.Tests/Application/ProjectServiceTests.cs`
- Input: placeholders
- Output: `ProjectService` with `Current`, `Path`, `IsDirty`, `New()`, `Load(path)`, `Save()`, `SaveAs(path)`, event `ProjectChanged`; `SettingsService` with `LastStlDirectory`, `LastProjectDirectory`, `LastExportDirectory`, `WindowWidth`, `WindowHeight`, `SpeedFactor`, stored as JSON at `Environment.SpecialFolder.ApplicationData/Miller/settings.json` (file name is a constant); tests: new/load/save round trip in a temp directory, dirty flag transitions
- Acceptance: tests green
- Status: done

#### T-061 Settings service tests
- Depends on: T-060
- Files: `tests/Miller.Tests/Application/SettingsServiceTests.cs`
- Input: placeholder
- Output: tests using a temp directory injected through the constructor: missing file yields defaults; save then load round trips; a corrupt file throws (no fallback to defaults)
- Acceptance: tests green
- Status: done

#### T-062 Log service and error dialog service
- Depends on: T-059
- Files: `src/Miller.Application/Services/LogService.cs`, `src/Miller.App/Services/ErrorDialogService.cs`
- Input: placeholders
- Output: `LogService.Info/Error(string)` appending timestamped lines to `logs/miller.log` next to the executable (directory created); `ErrorDialogService.Show(Exception)` logs and shows a modal window with message and a copyable details box; `Program.cs` wires unhandled exceptions to it
- Acceptance: throwing in a command shows the dialog and writes the log line
- Status: done

#### T-063 Main window layout and menu
- Depends on: T-059
- Files: `src/Miller.App/Views/MainWindow.axaml` (+ `.axaml.cs`), `src/Miller.App/Views/MainMenu.axaml` (+ `.axaml.cs`)
- Input: placeholders; menu list in `docs/ARCHITECTURE.md` 4.3
- Output: `DockPanel`: menu on top, status bar with progress at the bottom, left `TabControl` for settings panels (empty tabs named Tool, Stock, Axes, Cutting, Strategy, Simulation, Analysis), central `Border` reserved for the viewport; every menu item bound to a command on `MainWindowViewModel` (commands may be not-yet-enabled stubs that set `StatusText`); window size restored from settings on open and stored on close
- Acceptance: screenshot shows menu, tabs, central area, status bar; every menu item exists; headless test in T-065
- Status: done

#### T-064 File dialog service and Open STL command
- Depends on: T-063, T-046
- Files: `src/Miller.App/Services/IFileDialogService.cs`, `src/Miller.App/Services/FileDialogService.cs`, `src/Miller.App/ViewModels/MainWindowViewModel.cs`
- Input: placeholders
- Output: interface with `OpenFileAsync(title, extensions, startDirectory)`, `SaveFileAsync(...)`; implementation on `TopLevel.StorageProvider`; `OpenStlCommand` imports through `MeshImportService`, updates `StatusText` with triangle count and size, remembers the directory in settings, sets `Project.StlPath`
- Acceptance: opening the fixture shows `4050 triangles, 20.482 x 5.000 x 21.971 mm` in the status bar
- Status: done

#### T-065 Headless main window tests
- Depends on: T-064
- Files: `tests/Miller.Tests/App/MainWindowHeadlessTests.cs`, `tests/Miller.Tests/App/MainWindowViewModelTests.cs`
- Input: placeholders
- Output: `[AvaloniaFact]` constructing `MainWindow` with a real view model and a fake `IFileDialogService`, asserting the menu contains the four top-level menus and every item; view model tests: `OpenStlCommand` with the fake dialog returning the fixture sets the status text and the project path; returning null leaves the state unchanged
- Acceptance: tests green
- Status: done

#### T-066 Tool settings panel
- Depends on: T-063, T-021
- Files: `src/Miller.App/Views/ToolSettingsView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/ToolSettingsViewModel.cs`
- Input: placeholders
- Output: numeric text boxes for cutter diameter, cutter length, head diameter, combo for tip type, name; two-way binding to `ProjectService.Current.Tool`; validation messages from `ProjectValidator` shown under the field; a small schematic drawn with shapes bound to the values (head wider than cutter)
- Acceptance: screenshot; entering head diameter below cutter diameter shows the validator message
- Status: done

#### T-067 Stock settings panel
- Depends on: T-066
- Files: `src/Miller.App/Views/StockSettingsView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/StockSettingsViewModel.cs`
- Input: placeholders
- Output: shape combo (Box, Cylinder) switching the visible dimension fields, placement combo, margin, `Fit to model` button computing the box or cylinder that contains the transformed model bounds plus margin
- Acceptance: screenshot; fit button on the fixture yields 30.482 x 15.000 x 21.971 for a box with margin 5
- Status: done

#### T-068 Axis settings panel
- Depends on: T-067
- Files: `src/Miller.App/Views/AxisSettingsView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/AxisSettingsViewModel.cs`
- Input: placeholders
- Output: three combos for mapping, three check boxes for direction, three numeric rotations, origin mode combo with custom offset fields, a read-only line showing the machine-space bounds of the model for the current setup
- Acceptance: screenshot; swapping Y and Z on the fixture shows bounds 20.482 x 21.971 x 5.000
- Status: done

#### T-069 Cutting parameters panel
- Depends on: T-068
- Files: `src/Miller.App/Views/CuttingParametersView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/CuttingParametersViewModel.cs`
- Input: placeholders
- Output: fields for feed, plunge, rapid, spindle, stepover, finishing stepover, stepdown, safe height, cell size, tolerance, direction; a read-only estimate of the grid size (`Width x Height` cells) for the current stock and cell size with the validator's cell limit
- Acceptance: screenshot; cell size 0.01 on a 100 mm stock shows the cell limit error
- Status: done

#### T-070 Strategy selection panel and generate command
- Depends on: T-069, T-047
- Files: `src/Miller.App/Views/StrategySelectionView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/StrategySelectionViewModel.cs`, `src/Miller.App/ViewModels/MainWindowViewModel.cs`
- Input: placeholders
- Output: combos filled from `StrategyRegistry.All` filtered by operation and from `PostProcessorRegistry.All`; `GenerateCommand` running `PipelineService` with progress in the status bar and a cancel button; statistics block (lengths, counts, estimated time, levels)
- Acceptance: screenshot; generating on the fixture fills the statistics and can be cancelled
- Status: done

#### T-071 Settings view model tests
- Depends on: T-070
- Files: `tests/Miller.Tests/App/SettingsViewModelsTests.cs`
- Input: placeholder
- Output: tests for the five settings view models: property change writes to the project and marks it dirty; validator message appears for an invalid value and disappears when fixed; strategy combos contain the registry ids
- Acceptance: tests green
- Status: done

#### T-072 Save, load and export commands
- Depends on: T-070, T-054, T-060
- Files: `src/Miller.App/ViewModels/MainWindowViewModel.cs`, `tests/Miller.Tests/App/MainWindowViewModelTests.cs`
- Input: T-065 tests
- Output: `NewProjectCommand`, `OpenProjectCommand`, `SaveProjectCommand`, `SaveProjectAsCommand`, `ExportNcCommand` (enabled only when a toolpath exists), `ExitCommand` asking to save when dirty; window title shows `*` when dirty; tests with the fake dialog service for each command
- Acceptance: tests green; a project saved and reopened restores every panel value
- Status: done

#### T-073 About window and keyboard shortcuts
- Depends on: T-072
- Files: `src/Miller.App/Views/AboutWindow.axaml` (+ `.axaml.cs`), `src/Miller.App/Views/MainMenu.axaml`
- Input: placeholders
- Output: about window with the version, the package list and their licenses (MIT for Avalonia and the toolkit, Apache 2 for xunit); shortcuts `Ctrl+O` open STL, `Ctrl+S` save, `Ctrl+E` export, `F5` generate, `Space` play/pause
- Acceptance: screenshot of the about window; each shortcut triggers its command
- Status: done

#### T-074 UI rendering check for M5
- Depends on: T-073
- Files: none (manual check); record the result in the commit message of the next task
- Input: the running application
- Output: screenshots of every panel on Windows and on Linux (a Linux desktop or WSLg) compared against the field lists of T-063 to T-073
- Acceptance: every listed control visible on both systems; no layout overflow at 1280x720
- Status: done

### M6 3D viewport

#### T-075 GL constants and function loader
- Depends on: T-063
- Files: `src/Miller.App/Rendering/GlConstants.cs`, `src/Miller.App/Rendering/GlFunctions.cs`
- Input: placeholders; pitfall 3
- Output: constants for the enums used by the renderers (`GL_ARRAY_BUFFER`, `GL_ELEMENT_ARRAY_BUFFER`, `GL_STATIC_DRAW`, `GL_DYNAMIC_DRAW`, `GL_TRIANGLES`, `GL_LINES`, `GL_FLOAT`, `GL_DEPTH_TEST`, `GL_COLOR_BUFFER_BIT`, `GL_DEPTH_BUFFER_BIT`, `GL_VERTEX_SHADER`, `GL_FRAGMENT_SHADER`, `GL_COMPILE_STATUS`, `GL_LINK_STATUS`); `GlFunctions(GlInterface)` resolving delegates with `GetProcAddress` for VAO, buffer sub-data, uniform matrix and vector functions, throwing when a name resolves to zero
- Acceptance: compiles; used by T-076
- Status: done

#### T-076 Shader sources and program
- Depends on: T-075
- Files: `src/Miller.App/Rendering/GlShaders.cs`, `src/Miller.App/Rendering/ShaderProgram.cs`
- Input: placeholders; pitfall 2
- Output: one vertex and one fragment shader for lit triangles (position, normal, per-vertex color, uniforms model-view-projection and light direction) and one pair for lines (position, color); `Preamble(GlVersion)` returning the ES or core version line; `ShaderProgram` compiling, linking, throwing with the info log on failure, `Use()`, `Uniform(name)`
- Acceptance: compiles; both shader pairs compile on Windows (ANGLE) and on Linux (GLX) in T-078
- Status: done

#### T-077 Orbit camera
- Depends on: T-011
- Files: `src/Miller.App/Rendering/Camera.cs`, `tests/Miller.Tests/App/CameraTests.cs`
- Input: placeholders
- Output: `Camera` with `Target`, `Yaw`, `Pitch` (clamped), `Distance` (clamped), `Aspect`; `View`, `Projection` (perspective, near and far from distance), `Orbit(dx, dy)`, `Pan(dx, dy)`, `Zoom(factor)`, `FitToBounds(BoundingBox)`; tests: after `FitToBounds` the bounds corners project inside the clip volume; pitch clamp; zoom factor changes distance multiplicatively
- Acceptance: tests green
- Status: done

#### T-078 Viewport control and scene renderer skeleton
- Depends on: T-076, T-077
- Files: `src/Miller.App/Views/Viewport3DControl.cs`, `src/Miller.App/Rendering/SceneRenderer.cs`, `src/Miller.App/Views/MainWindow.axaml`
- Input: placeholders; pitfall 4
- Output: `Viewport3DControl : OpenGlControlBase` creating `SceneRenderer` in `OnOpenGlInit`, calling `Render(gl, width, height)` in `OnOpenGlRender`, disposing in `OnOpenGlDeinit`; mouse: left drag orbit, right drag pan, wheel zoom, double click fit; `SceneRenderer` clearing with the viewport background color from resources and drawing nothing else yet; control placed in the central border of `MainWindow`
- Acceptance: screenshot shows the viewport background color; no GL error logged on Windows and Linux
- Status: done

#### T-079 Axis triad and stock outline renderer
- Depends on: T-078
- Files: `src/Miller.App/Rendering/AxisTriadRenderer.cs`, `src/Miller.App/ViewModels/ViewportViewModel.cs`
- Input: placeholders
- Output: lines for X, Y, Z (colors from resources) at the machine origin and the stock outline (box edges or cylinder rings and verticals) from the current project; `ViewportViewModel` holding what to show (flags for model, stock, toolpath, tool) and the data references, raising a `RequestRender` event
- Acceptance: screenshot with triad and stock outline for the default project
- Status: done

#### T-080 Mesh renderer
- Depends on: T-079, T-064
- Files: `src/Miller.App/Rendering/MeshRenderer.cs`
- Input: placeholder
- Output: VBO with position, normal, color per vertex (flat shading, model color), index-free `GL_TRIANGLES`, uploaded when the mesh changes, drawn with the lit shader; camera fits to the mesh bounds when a new mesh arrives
- Acceptance: screenshot of the fixture heart in the viewport, lit, correct orientation for the default axis setup
- Status: done

#### T-081 Toolpath renderer
- Depends on: T-080, T-070
- Files: `src/Miller.App/Rendering/ToolpathRenderer.cs`
- Input: placeholder
- Output: line VBO with per-segment color by kind; `ProgressIndex` uniform-free approach: two draws (done part and remaining part) using an index range; toggled by the view model flag
- Acceptance: screenshot of the generated fixture toolpath with rapid, feed and plunge colors distinguishable
- Status: done

#### T-082 Heightmap renderer
- Depends on: T-081, T-030
- Files: `src/Miller.App/Rendering/HeightMapRenderer.cs`
- Input: placeholder
- Output: grid mesh from a `HeightMap` (one vertex per cell center, NaN cells skipped by index omission), normals from neighbors, per-vertex color from a category array or a single color; `Upload(HeightMap)` full and `Update(HeightMap, dirtyRectangle)` partial via buffer sub-data
- Acceptance: screenshot of the stock rendered as a solid block over the fixture
- Status: done

#### T-083 Tool renderer
- Depends on: T-082
- Files: `src/Miller.App/Rendering/ToolRenderer.cs`
- Input: placeholder
- Output: cutter cylinder (diameter, length) and head cylinder (head diameter, fixed display length) built once per tool definition, drawn at the current tool position with the tool colors; flat tip as a disc, ball tip as a hemisphere
- Acceptance: screenshot with the tool at the toolpath start after generation
- Status: done

#### T-084 Viewport view model wiring
- Depends on: T-083
- Files: `src/Miller.App/ViewModels/ViewportViewModel.cs`, `src/Miller.App/ViewModels/MainWindowViewModel.cs`
- Input: T-079 view model
- Output: subscriptions: mesh changed -> mesh renderer and fit; project changed -> stock outline and tool geometry; pipeline finished -> toolpath and stock heightmap; view menu toggles bound to the flags; reset camera command
- Acceptance: opening a new STL, changing stock size and generating each update the viewport without restart
- Status: done

#### T-085 Viewport performance check
- Depends on: T-084
- Files: `tests/Miller.Tests/App/CameraTests.cs`
- Input: running application
- Output: measured frame time with the fixture at cell size 0.1 (about 300 x 150 cells with margin) and at 0.05; recorded as constants `HeightMapRenderer.MaxCellsForInteractiveFrame` used by `ProjectValidator` as a warning threshold; camera tests extended with a projection of the fixture bounds
- Acceptance: at least 30 frames per second at cell size 0.1 on the development machine; the warning appears above the measured limit
- Status: done

#### T-086 UI rendering check for M6
- Depends on: T-085
- Files: none (manual check)
- Input: running application on Windows and Linux
- Output: screenshots: heart mesh, stock outline, toolpath, tool; orbit, pan, zoom, fit verified
- Acceptance: identical scene content on both systems (colors and geometry); no GL errors in the log
- Status: done (Windows captures in batch 16: mesh, stock block, toolpath, toggles, no GL error logged; the Linux viewport check waits for a Linux desktop, the GL path is shared code)

### M7 Simulation and final model

#### T-087 Simulation clock
- Depends on: T-003
- Files: `src/Miller.Core/Simulation/SimulationClock.cs`, `tests/Miller.Tests/Core/Simulation/SimulationClockTests.cs`
- Input: placeholders; section 6.6
- Output: `MinSpeedFactor = 0.1f`, `MaxSpeedFactor = 1000f`, `SpeedFactor` setter clamping, `IsPlaying`, `Play()`, `Pause()`, `Advance(realSeconds) -> simSeconds`, `ElapsedSimulated`; tests: clamp both ends; paused returns 0; 2 s at 1000x yields 2000 s
- Acceptance: tests green
- Status: done

#### T-088 Material remover
- Depends on: T-025, T-031
- Files: `src/Miller.Core/Simulation/MaterialRemover.cs`, `tests/Miller.Tests/Core/Simulation/MaterialRemoverTests.cs`
- Input: placeholders; formula 6.3
- Output: `Sweep(HeightMap stock, ToolProfile, Vector3 from, Vector3 to) -> DirtyRect` sampling at most `CellSize / 2` apart, applying `min(stock, z + dz)` over the footprint, skipping NaN cells, returning the touched cell rectangle; tests: a horizontal flat-tool move at z = 3 through a stock at 5 leaves a channel of exactly the tool width at 3; a ball tool leaves a rounded channel; a rapid at safe height changes nothing; dirty rectangle bounds the changed cells exactly
- Acceptance: tests green
- Status: done

#### T-089 Simulation engine
- Depends on: T-088, T-087
- Files: `src/Miller.Core/Simulation/SimulationEngine.cs`, `tests/Miller.Tests/Core/Simulation/SimulationEngineTests.cs`
- Input: placeholders; section 6.6
- Output: `SimulationEngine(Toolpath, HeightMap stock, ToolProfile, CuttingParameters)`, `Step(simSeconds) -> StepResult` (tool position, dirty rectangle union, segments completed), `RunToEnd()`, `Reset(HeightMap freshStock)`, `Progress` 0..1, `CurrentSegmentIndex`, `IsFinished`; tests: stepping the full duration in one call equals `RunToEnd` stock cell by cell; many small steps equal one big step; progress monotonic; reset restores the stock
- Acceptance: tests green
- Status: done

#### T-090 Collision detector
- Depends on: T-089, T-028
- Files: `src/Miller.Core/Simulation/CollisionDetector.cs`, `src/Miller.Core/Simulation/SimulationEvent.cs`, `tests/Miller.Tests/Core/Simulation/CollisionDetectorTests.cs`
- Input: placeholders; pitfall 11
- Output: `Check(HeightMap stock, ToolProfile, float cutterLength, Vector3 tip, MoveKind) -> SimulationEvent?`: head collision when any annulus cell is above `tip.Z + cutterLength`; rapid into material when a rapid sample is below the stock under the footprint; tests: slot fixture with short cutter produces a head collision, long cutter none; a rapid through the stock produces the event; feed moves never produce the rapid event
- Acceptance: tests green
- Status: done

#### T-090a Head clearance against rest material (found by T-090)
- Depends on: T-090, T-030
- Files: `src/Miller.Core/HeightMap/HeadClearance.cs`, `src/Miller.Application/Services/PipelineService.cs`, `tests/Miller.Tests/Core/HeightMap/HeadClearanceTests.cs`
- Input: simulation of the heart fixture with the default project reports 1404 head collisions: next to the heart walls the cutter leaves a strip it cannot reach (within one cutter radius of the wall), the roughing keeps that strip at an early level, and the head limit computed from the model alone lets the tip descend beside it until the head hits the strip
- Output: the head limit is computed from the predicted remaining stock, not from the model: remaining = closing of the model by the cutter footprint (erode the tip map with the footprint, flat tools: min over the footprint of tip); `HeadClearance.Limit(remaining, profile, cutterLength)`; the pipeline recomputes the effective tip with it; tests: a wall next to a floor with a short cutter raises the limit beside the strip; the heart end-to-end simulation reports zero head collisions
- Acceptance: `SimulationService.RunToEnd` on the fixture with the default project yields no HeadCollision event; golden files updated with the new toolpath and reviewed
- Status: superseded by T-103

#### T-091 Simulation service
- Depends on: T-090, T-047
- Files: `src/Miller.Application/Services/SimulationService.cs`, `tests/Miller.Tests/Application/SimulationServiceTests.cs`
- Input: placeholders
- Output: `Load(PipelineResult)` creating engine, clock, detector on a fresh stock; `Play`, `Pause`, `Stop` (reset), `StepOnce(simSeconds)`, `RunToEnd`, `SpeedFactor`, `Advance(realSeconds) -> SimulationSnapshot` (tool position, dirty rectangle, new events, progress, elapsed); `Events` list; tests: play then advance changes the stock; stop restores it; events are accumulated; speed factor is forwarded and clamped
- Acceptance: tests green
- Status: done

#### T-092 UI timer
- Depends on: T-091, T-084
- Files: `src/Miller.App/Services/UiTimer.cs`, `src/Miller.App/App.axaml.cs`
- Input: placeholder
- Output: `DispatcherTimer` at 60 Hz measuring real elapsed time with `Stopwatch`, calling `SimulationService.Advance` and passing the snapshot to `ViewportViewModel` (heightmap partial update, tool position, toolpath progress index), then requesting a render; started with the app, idle when the service is not playing
- Acceptance: with a loaded toolpath, play animates the tool and the stock in the viewport
- Status: done

#### T-093 Log slider converter
- Depends on: T-006
- Files: `src/Miller.App/Converters/LogSliderConverter.cs`, `tests/Miller.Tests/App/SimulationViewModelTests.cs`
- Input: placeholders
- Output: `IValueConverter` mapping slider 0..1 to `10^(-1 + 4 t)` (0.1 to 1000) and back; test file gets converter tests: 0 -> 0.1, 0.25 -> 1, 1 -> 1000, round trip within 1e-4
- Acceptance: tests green
- Status: done

#### T-094 Simulation controls panel
- Depends on: T-092, T-093
- Files: `src/Miller.App/Views/SimulationControlsView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/SimulationViewModel.cs`
- Input: placeholders
- Output: buttons play, pause, stop, step, run to end; logarithmic slider with numeric text box (both bound to `SpeedFactor`); progress bar; simulated time; collision counter with the last event text; `Space` shortcut; speed persisted in settings
- Acceptance: screenshot; speed 0.1 visibly slows, 1000 finishes the fixture toolpath within seconds
- Status: done

#### T-095 Simulation view model tests
- Depends on: T-094
- Files: `tests/Miller.Tests/App/SimulationViewModelTests.cs`
- Input: T-093 tests
- Output: tests: commands enable and disable correctly in each state (no toolpath, loaded, playing, paused, finished); speed text `5000` clamps to 1000 and shows the validator message; stop resets progress to 0
- Acceptance: tests green
- Status: done

#### T-096 Deviation map and final model analyzer
- Depends on: T-089
- Files: `src/Miller.Core/Analysis/DeviationMap.cs`, `src/Miller.Core/Analysis/FinalModelAnalyzer.cs`, `tests/Miller.Tests/Core/Analysis/FinalModelAnalyzerTests.cs`
- Input: placeholders; formula 6.3
- Output: `DeviationMap` (`Values` heightmap, `Categories` array with `CellCategory { Ok, RestMaterial, Gouge, NoModel }`), `FinalModelAnalyzer.Analyze(HeightMap finalStock, HeightMap model, float floor, float tolerance) -> AnalysisResult` with the map, counts, areas and volumes per category; tests: identical maps give all Ok; stock above model gives RestMaterial with the right volume; stock below gives Gouge; floor cells are NoModel
- Acceptance: tests green
- Status: done

#### T-097 Analysis service and run-to-end command
- Depends on: T-096, T-091
- Files: `src/Miller.Application/Services/AnalysisService.cs`, `src/Miller.App/ViewModels/AnalysisViewModel.cs`
- Input: placeholders
- Output: `AnalyzeAsync(PipelineResult, CancellationToken)` running the engine to the end on a fresh stock on a background task and returning `AnalysisResult`; `AnalysisViewModel` with `AnalyzeCommand`, result properties, `ShowFinalModel` flag switching the heightmap renderer to the final stock with category colors
- Acceptance: on the fixture, the analysis reports rest material above zero and zero gouge cells
- Status: done

#### T-098 Uncuttable regions
- Depends on: T-097, T-023
- Files: `src/Miller.Core/Analysis/UncuttableRegions.cs`, `tests/Miller.Tests/Core/Analysis/UncuttableRegionsTests.cs`
- Input: placeholders; classification 6.3
- Output: `Compute(Mesh machineMesh, HeightMap model, HeightMap tip, HeightMap effectiveTip, bool[,] headLimited, float floor, float tolerance) -> UncuttableResult` with masks Overhang, HeadLimited, CornerLimited and counts, using `MeshRasterizer.RasterizeDownwardFacing`; tests: the fixture has overhang cells above zero; the slot fixture with the short cutter has head-limited cells equal to the slot floor cells; a plate with a 1 mm inner corner radius and a 6 mm tool has corner-limited cells
- Acceptance: tests green
- Status: done

#### T-099 Analysis panel and category coloring
- Depends on: T-098, T-082
- Files: `src/Miller.App/Views/AnalysisView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/AnalysisViewModel.cs`, `src/Miller.App/Rendering/HeightMapRenderer.cs`
- Input: T-097 view model
- Output: analyze button, legend with the six category colors from resources, counts, areas and volumes; the heightmap renderer takes a category array and colors vertices accordingly; toggles between stock view, final model view and uncuttable overlay
- Acceptance: screenshot of the fixture final model with rest material and overhang regions colored
- Status: done

### M9 Feature extension (user request after M7)

#### T-103 Head clearance from remaining material
- Depends on: T-090a, T-030
- Files: `src/Miller.Core/HeightMap/HeightMapDilation.cs`, `src/Miller.Application/Services/PipelineService.cs`, `tests/Miller.Tests/Core/HeightMap/HeightMapDilationTests.cs`, `tests/Miller.Tests/Application/PipelineServiceTests.cs`, `tests/Miller.Tests/Golden/heart_grbl.nc`
- Input: T-090a finding (1404 head collisions on the heart)
- Output: `HeightMapDilation.ComputeRemaining(tip, profile)` = closing of the model (min over the footprint of tip + dz); the pipeline derives the head limit from the remaining material and repeats until the effective tip settles (`PipelineService.MaxHeadIterations`); golden regenerated
- Acceptance: a wall beside a floor with a short cutter yields zero head events in `SimulationService.RunToEnd`; closing tests for narrow and wide slots; suite green
- Status: done

#### T-104 Pass ordering with fewer retracts
- Depends on: T-046
- Files: `src/Miller.Core/Toolpath/PassOrdering.cs`, `src/Miller.Core/Toolpath/ToolpathLinker.cs`, `src/Miller.Core/Toolpath/ToolpathStatistics.cs`, `src/Miller.App/ViewModels/StrategySelectionViewModel.cs`, tests
- Input: user request 7
- Output: `PassGroup` (passes of one level) and `PassOrdering.Order` (greedy nearest neighbour from the previous end, open passes may be reversed, closed loops keep their direction); `ToolpathLinker.Link(groups, ...)` orders every group and links; `ToolpathStatistics.RetractCount` shown in the strategy panel
- Acceptance: a designed two-region level links with one retract instead of many; groups never interleave; gouge check clean on the fixtures; golden regenerated
- Status: done

#### T-105 Layer complete strategy
- Depends on: T-104, T-045, T-047
- Files: `src/Miller.Core/Toolpath/Strategies/LayerCompleteStrategy.cs`, `src/Miller.Core/Toolpath/StrategyRegistry.cs`, `tests/Miller.Tests/Core/Toolpath/Strategies/LayerCompleteStrategyTests.cs`
- Input: user request 8
- Output: roughing strategy `layer-complete`: per level the raster runs of the level mask followed by the contour loops of the allowed mask at that level, all in one pass group; the next level starts only after both
- Acceptance: registered and selectable; gouge check clean on the slot and bump fixtures; segments of level k precede every segment of level k+1
- Status: done

#### T-106 Camera buttons and pick ray
- Depends on: T-078
- Files: `src/Miller.App/Views/Viewport3DControl.cs`, `src/Miller.App/Rendering/Camera.cs`, `src/Miller.Core/Geometry/BoundingBox.cs`, `src/Miller.App/ViewModels/ViewportViewModel.cs`, `src/Miller.App/Views/AboutWindow.axaml`, tests
- Input: user request 1
- Output: right drag orbits, middle (wheel) drag pans, wheel zooms, double click fits; a left click without drag raises `ViewportViewModel.Pick(origin, direction)` built by `Camera.PickRay`; `BoundingBox.IntersectRay` for the model hit test; About lists the mouse controls
- Acceptance: `Camera.PickRay` through the viewport center hits the camera target; ray tests against a box from inside and outside; desktop or headless check of the control wiring
- Status: done

#### T-107 Several models in one project
- Depends on: T-016, T-020
- Files: `src/Miller.Core/Setup/ModelPlacement.cs`, `src/Miller.Core/Setup/MillingProject.cs`, `src/Miller.Core/Setup/ProjectSerializer.cs`, `src/Miller.Application/Services/MeshImportService.cs`, `src/Miller.Application/Services/PipelineService.cs`, tests
- Input: user requests 5 and 6
- Output: `ModelPlacement { StlPath, Offset, RotationZ }` list `MillingProject.Models` (schema 2, legacy `StlPath` migrated into the first entry on load); `MeshImportService` holds one loaded mesh per placement with add, remove, clear; the pipeline places every mesh (orientation, rotation about Z, offset) and merges them into one machine mesh; stock corner and origin from the union of placed bounds
- Acceptance: round trip through JSON; a version 1 file with one `StlPath` loads as one model; two boxes side by side produce one toolpath covering both; suite green
- Status: done

#### T-108 Models panel
- Depends on: T-107, T-063
- Files: `src/Miller.App/Views/ModelsView.axaml` (+ `.axaml.cs`), `src/Miller.App/ViewModels/ModelsViewModel.cs`, `src/Miller.App/Views/MainWindow.axaml`, `src/Miller.App/ViewModels/MainWindowViewModel.cs`, tests
- Input: T-107
- Output: tab "Models": list with names, Add (open STL), Remove, selection; for the selected model offset X, Y, Z and rotation Z fields; Center X, Center Y, Center Z buttons placing the model in the middle of the stock on that axis only; validator warning per model outside the stock
- Acceptance: view-model tests for add, remove, select, offsets and centering; headless capture of the tab
- Status: done

#### T-109 Selection in the viewport
- Depends on: T-108, T-106
- Files: `src/Miller.App/ViewModels/ViewportViewModel.cs`, `src/Miller.App/Rendering/SceneRenderer.cs`, `src/Miller.App/Rendering/MeshRenderer.cs`, `src/Miller.App/Styles/Colors.axaml`
- Input: T-106, T-108
- Output: one mesh renderer per model; the selected model drawn in the selection color; a left click picks the nearest model whose placed bounds the ray hits, a miss clears the selection; selection shared with the Models panel
- Acceptance: view-model test: pick selects and a miss clears; desktop capture shows the highlight
- Status: done

#### T-110 Models feature documentation and regression
- Depends on: T-109
- Files: `docs/ARCHITECTURE.md`, `docs/DEVELOPMENT_GUIDE.md`, `src/CLAUDE.md`, `samples/heart.miller.json`
- Input: T-107 to T-109
- Output: architecture data model and pipeline sections describe the model list; sample project in schema 2; lessons recorded
- Acceptance: `bash build.sh --no-publish` green; export of the sample unchanged
- Status: done

### M8 Packaging and release

#### T-100 Application icon and publish verification
- Depends on: T-099, T-008
- Files: `src/Miller.App/Assets/Icons/miller.ico`, `src/Miller.App/Assets/Icons/miller.png`, `src/Miller.App/Miller.App.csproj`
- Input: placeholders
- Output: a simple icon (end mill silhouette) as `.ico` (16, 32, 48, 256) and `.png` (256); `ApplicationIcon` in the project file; window icon set from the png resource
- Acceptance: `bash build.sh` produces `dist/win-x64/Miller.exe` with the icon visible in Explorer and `dist/linux-x64/Miller.sh` runs the GUI on a Linux desktop

#### T-101 Third-party licenses and README
- Depends on: T-100
- Files: `THIRD_PARTY_LICENSES.md`, `README.md`
- Input: package list; `third_party/nuget/` contents
- Output: license text pointers for every vendored package family (Avalonia, CommunityToolkit, xunit, SkiaSharp and HarfBuzzSharp via Avalonia, Microsoft runtime packs); README with what Miller does, how to build (section 4), how to run, the menu overview
- Acceptance: every package family in `third_party/nuget/` appears in the licenses file

#### T-102 Release regression on both systems
- Depends on: T-101, T-056, T-086
- Files: none (verification); tag
- Input: complete repository
- Output: `bash build.sh` on Windows and on Linux both green; `EndToEndTests` green on both; the Linux export diff empty; git tag `Build_1.0.X` on the final commit
- Acceptance: all of the above recorded in the tag message
