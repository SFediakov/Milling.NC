# Miller - Architecture

Miller converts an STL model into an NC (G-code) program for a 3-axis CNC milling
machine, previews the milling simulation and previews the final machined result.
This document is the architecture decision record and the module map. The task
list that implements it is in `docs/DEVELOPMENT_GUIDE.md`.

## 1. Goals and constraints

| # | Requirement (from the task definition) | Where it is satisfied |
|---|---|---|
| G1 | One code base for Windows and Linux; only the start file differs (`Miller.exe` vs `Miller.sh`) | Section 7, `build.sh`, `launchers/Miller.sh` |
| G2 | GUI with a basic menu | `src/Miller.App/Views/MainWindow.axaml`, `src/Miller.App/Views/MainMenu.axaml` |
| G3 | Load `.stl`, produce `.nc` | `src/Miller.Core/Io/StlReader.cs`, `src/Miller.Core/GCode/GrblPostProcessor.cs`, `src/Miller.Application/Services/ExportService.cs` |
| G4 | Slicer decomposing the STL into separate milling steps | `src/Miller.Core/Slicing/Slicer.cs`, `src/Miller.Core/Slicing/MillingStep.cs` |
| G5 | Cutter diameter and cutter length before the head ("glowica") of larger diameter | `src/Miller.Core/Setup/ToolDefinition.cs`, `src/Miller.Core/HeightMap/HeadClearance.cs`, `src/Miller.App/Views/ToolSettingsView.axaml` |
| G6 | Change processing speed | `src/Miller.Core/Setup/CuttingParameters.cs` (feed, plunge, rapid, spindle), `src/Miller.App/Views/CuttingParametersView.axaml` |
| G7 | Clearance of passes | `src/Miller.Core/Setup/CuttingParameters.cs` (stepover, finishing stepover, stepdown, safe height) |
| G8 | Stock material: cubic, rectangular, circular 3D forms with dimensions | `src/Miller.Core/Setup/StockDefinition.cs`, `src/Miller.Core/Simulation/StockModel.cs`, `src/Miller.App/Views/StockSettingsView.axaml` |
| G9 | Axis positioning, direction and rotation | `src/Miller.Core/Setup/AxisSetup.cs`, `src/Miller.App/Views/AxisSettingsView.axaml` |
| G10 | Simulation preview of head movement and material removal, speed 0.1x to 1000x | `src/Miller.Core/Simulation/SimulationEngine.cs`, `src/Miller.Core/Simulation/MaterialRemover.cs`, `src/Miller.Core/Simulation/SimulationClock.cs`, `src/Miller.App/Rendering/HeightMapRenderer.cs`, `src/Miller.App/Rendering/ToolRenderer.cs`, `src/Miller.App/Views/SimulationControlsView.axaml` |
| G11 | Preview of the final cut model including inaccuracy and uncuttable areas | `src/Miller.Core/Analysis/FinalModelAnalyzer.cs`, `src/Miller.Core/Analysis/UncuttableRegions.cs`, `src/Miller.App/Views/AnalysisView.axaml` |
| G12 | Modular, exchangeable routing algorithms | `src/Miller.Core/Toolpath/IToolpathStrategy.cs`, `src/Miller.Core/Toolpath/StrategyRegistry.cs`, `src/Miller.Core/Toolpath/Strategies/ZLayerByLayerStrategy.cs`, `src/Miller.Core/Toolpath/Strategies/ThreeAxisFreedomStrategy.cs` |

Project rules that constrain the design (root `CLAUDE.md`):

- Buildable on Linux without code modification. Same build script on both systems.
- No web references at build time. All packages are vendored in `third_party/nuget/`
  (git-ignored; `scripts/vendor-packages.sh` fills the folder once per clone).
- No fallbacks, no dual code paths, no hidden switches.
- Colors declared in exactly one file (`src/Miller.App/Styles/Colors.axaml`).
- Version string `Build_Y.Z.X` incremented on every app change.
- Build output only in `bin/`, `obj/`, `build/`, `out/`, `dist/`.
- File names `.env`, `deployment.json`, `tokens.json`, `*.db` are protected by the
  repository guards and must not be used for anything.

## 2. Technology decision

### 2.1 GUI stack research summary

| Candidate | Windows + Linux | 3D viewport | Offline packaging | License | Automated UI tests |
|---|---|---|---|---|---|
| Avalonia 12 (.NET 10) | Official, one rendered control set (Skia) | `OpenGlControlBase` gives a GL context inside the window; the official ControlCatalog sample renders a 3D mesh on both ANGLE (Windows) and GLX (Linux) | Local NuGet folder feed, `NuGet.config` with cleared sources | MIT | `Avalonia.Headless.XUnit` (xunit v3), `CaptureRenderedFrame` for Skia content |
| PySide6 + pyqtgraph (Python) | Yes | `GLViewWidget` + `GLMeshItem` ready made | pip wheelhouse; PySide6 wheels for two platforms are several hundred MB; `.exe` must be built on Windows (PyInstaller does not cross-build) | LGPLv3 | pytest-qt, no headless GL |
| Electron or Tauri + three.js | Yes | three.js, excellent | Chromium bundle or Rust toolchain (Rust not installed); heavy `node_modules` vendoring | MIT | Playwright |
| C++ + Qt 6 | Yes | Qt3D / QOpenGLWidget | Vendoring Qt is impractical; no gcc/clang on the development machine | LGPLv3 / commercial | Qt Test |
| .NET MAUI | No Linux desktop | none | n/a | MIT | n/a |
| Uno Platform | Yes | none built in | NuGet | Apache 2 | yes |

Decision: **.NET 10 + Avalonia 12.1.2**.

Reasons, in order of weight:

1. One Linux container publishes both `win-x64` and `linux-x64` self-contained
   binaries from the same source (`dotnet publish -r <rid>`), which is the
   cleanest way to satisfy G1 and "buildable on Linux".
2. The compiler catches most mistakes of a less capable developer before runtime.
3. The .NET SDK is already installed and used by the owner's other projects.
4. Vendoring is a folder of `.nupkg` files plus one `NuGet.config`.
5. Avalonia has a documented OpenGL control and a headless test platform.

Rejected: Python (no cross-built `.exe`, huge wheel vendoring), web shells
(browser engine bundle and `node_modules` conflict with the vendoring rule),
C++/Qt (toolchain and vendoring cost), MAUI (no Linux).

### 2.2 Pinned packages

All versions are pinned in `Directory.Packages.props` (central package
management). No other packages may be added without a task in the guide.

| Package | Version | Used by |
|---|---|---|
| Avalonia | 12.1.2 | Miller.App |
| Avalonia.Desktop | 12.1.2 | Miller.App |
| Avalonia.Themes.Fluent | 12.1.2 | Miller.App, Miller.Tests |
| Avalonia.Fonts.Inter | 12.1.2 | Miller.App (same font on both OS) |
| CommunityToolkit.Mvvm | 8.4.2 | Miller.App (ObservableObject, RelayCommand) |
| Avalonia.Headless.XUnit | 12.1.2 | Miller.Tests |
| xunit.v3 | 3.2.2 | Miller.Tests (exact version the headless package depends on) |
| xunit.runner.visualstudio | 3.1.5 | Miller.Tests |
| Microsoft.NET.Test.Sdk | 18.10.0 | Miller.Tests |

Core and Application use only the base class library: `System.Numerics`
(`Vector3`, `Matrix4x4`) and `System.Text.Json`. Single precision float is
sufficient: parts are tens to hundreds of millimetres and G-code is written with
three decimals.

## 3. Layers and dependency rules

```
+---------------------------------------------------------------+
| Miller.App        (Avalonia 12)  Views, ViewModels, Rendering  |  UI
+---------------------------------------------------------------+
        | references
        v
+---------------------------------------------------------------+
| Miller.Application   Services, Validation, Persistence         |  Application
+---------------------------------------------------------------+
        | references
        v
+---------------------------------------------------------------+
| Miller.Core   Geometry, Io, Setup, HeightMap, Slicing,         |  Domain
|               Toolpath, GCode, Simulation, Analysis            |
+---------------------------------------------------------------+
        | references
        v
+---------------------------------------------------------------+
| Miller.Solver   Route solver facades (no domain types)         |  Numeric
+---------------------------------------------------------------+
        | P/Invoke (both Core and Solver)
        v
+---------------------------------------------------------------+
| Miller.Native   miller_native.dll / libmiller_native.so (C11)  |  Native
|                 the whole toolpath generation                  |
+---------------------------------------------------------------+

Miller.Tests references all four .NET projects.
```

Rules:

- `Miller.Native` is the native C library that runs the whole toolpath
  generation (T-134): mesh transform, rasterizing, stock, reach map, head
  clearance, slicing, separation, islands, both strategies with lattice, cave
  tree and route solver, route writer, gouge check, simplifier and statistics.
  `mn_generate` runs the complete pipeline in one call; every stage is also
  exported on its own. It is C11 with no dependency but the C runtime and the
  thread API of the system, reproduces the former C# float results bit for bit
  (MathF.Max NaN rules, ties-to-even rounding, saturating conversions, the fused
  multiply-add of `Vector3.Transform`) and is built by CMake from the
  `Miller.Solver` project on every build. Errors come back as status codes with
  a thread-local message; the facades turn them into the same .NET exceptions
  as before.
- `Miller.Solver` references nothing managed. It holds the P/Invoke facades of
  the routing (surface polyline, pair costs, turn fine, route solver, budget)
  and knows no domain type.
- The C# classes of the generation (`ReachMap`, `HeadClearance`, `Slicer`,
  `SeparationRegion`, the strategies, `RouteWriter`, `GougeChecker`,
  `ToolpathSimplifier`, `ToolpathStatistics` and the rest) keep their public
  signatures and forward to the native library (`Miller.Core/Native/CoreNative.cs`);
  they hold no algorithm of their own any more. `ToolpathGeneration.Run` builds
  the job for `mn_generate` and reads every map, mask, plan and segment back.
- `Miller.Core` references `Miller.Solver` and no package. It has no file
  dialogs, no threads of its own, no timers. It is deterministic: same input,
  same output, on every OS.
- `Miller.Application` references `Miller.Core` only. It owns long-running
  orchestration (`IProgress<T>`, `CancellationToken`), file I/O for projects and
  exports, and user settings. It has no Avalonia reference, so any UI type used
  here fails to compile.
- `Miller.App` references `Miller.Application` and Avalonia packages. View models
  never call `Miller.Core` algorithms directly; they call services.
- Platform differences are confined to the publish step. No `#if WINDOWS`,
  no `RuntimeInformation` branches in code. The only runtime discrimination is
  the OpenGL shader preamble chosen from the `GlVersion` that Avalonia reports
  (GLES on Windows ANGLE, desktop GL on Linux), which is a graphics API
  difference, not an OS difference.

## 4. Module map

Paths are relative to the repository root. Every file listed exists as a
placeholder; the task that implements it is written in the placeholder header.

### 4.1 src/Miller.Core

| Folder | File | Responsibility |
|---|---|---|
| Geometry | `Mesh.cs` | Triangle soup: vertex array, triangle index array, per-triangle normal; `Bounds`, `Transform(Matrix4x4)` |
| Geometry | `Triangle.cs` | Three `Vector3` corners, normal, `MinZ`, `MaxZ` |
| Geometry | `BoundingBox.cs` | Min/Max `Vector3`, `Size`, `Center`, `Union`, `Contains` |
| Io | `StlReader.cs` | Detects binary vs ASCII by the size rule (84 + 50 x n == file length), dispatches to a parser, returns `Mesh` + `StlImportReport` |
| Io | `StlBinaryParser.cs` | 80-byte header, uint32 count, 50-byte records, little endian |
| Io | `StlAsciiParser.cs` | `solid`/`facet normal`/`outer loop`/`vertex`/`endsolid` grammar, invariant culture |
| Io | `StlImportReport.cs` | Triangle count, bounds, degenerate triangle count, format detected |
| Setup | `ToolDefinition.cs` | `CutterDiameter`, `CutterLength` (usable length below the head), `HeadDiameter` (> cutter), `TipType` (Flat, Ball), `Name` |
| Setup | `StockDefinition.cs` | `Shape` (Box, Cylinder); Box: `SizeX`, `SizeY`, `SizeZ`; Cylinder: `Diameter`, `Height`; `Placement` (AutoFitWithMargin, Explicit) and `Margin`; auto-fit alignment of the model union per axis `AlignX`, `AlignY`, `AlignZ` (`StockAlignment` Min, Center, Max; defaults Center, Center, Max) |
| Setup | `AxisSetup.cs` | Mapping of model axes to machine X, Y, Z; direction sign per axis; rotation angles (degrees) about X, Y, Z; `OriginMode` (StockCornerMinXYMinZ, StockCornerMinXYTopZ, StockCenterTopZ, Custom); `ToMatrix()` |
| Setup | `CuttingParameters.cs` | `FeedRate`, `PlungeRate`, `RapidRate` (mm/min), `SpindleRpm`, `Stepover`, `Stepdown`, `SafeHeight`, `CellSize`, `Tolerance`, `MillingDirection` (Zigzag, OneWay) |
| Setup | `MillingProject.cs` | Aggregate of all setup objects + `Models` (list of `ModelPlacement`), `RoutingStrategyId`, `PostProcessorId`, `CutScope` (Everything, Separation), `MinIslandVolume` (mm3, separation only), `ReachPercent` (reach rule, default 50); JSON serializable, schema 3 (the legacy strategy id `three-axis-precise` is mapped to `three-axis-freedom` on load (schema 1 `StlPath`, schema 2 `RoughingStrategyId`, `FinishingStrategyId` and `Parameters.Direction` are read and dropped on load) |
| Setup | `ModelPlacement.cs`, `ModelLayout.cs` | One STL with offset and rotation about Z; the layout places every model (orientation, rotation, offset), takes machine zero from the stock corner around the anchor (`AnchorBounds`: the union of the models before their offsets, so an offset moves a model inside the stock) and merges the machine meshes; `StockBoundsMachine` is the stock box in machine space (corner at minus the origin offset), `AlignedOffset` puts one model at the stock minimum, middle or maximum per axis in one step |
| Setup | `MillingPreset.cs` | Named copy of Tool, Axes, Parameters, `RoutingStrategyId`, `PostProcessorId`, `CutScope`, `MinIslandVolume`, `ReachPercent`; `FromProject`, `ApplyTo` (deep copies through the project JSON options) |
| Setup | `ProjectSerializer.cs` | `System.Text.Json` read/write with invariant culture and schema version |
| HeightMap | `HeightMap.cs` | Uniform grid: `OriginX`, `OriginY`, `CellSize`, `Width`, `Height`, `float[] Z`; `float.NaN` = no material / outside stock; cell-world conversions; `Clone()`; `Min()`, `Max()` |
| HeightMap | `MeshRasterizer.cs` | Model map: top-down rasterization of triangles, max Z per cell; uncovered cells = `floor` value |
| HeightMap | `ToolProfile.cs` | Footprint of a tool on the grid: list of `(dx, dy, dz)` where `dz(d) = 0` (flat) or `r - sqrt(r^2 - d^2)` (ball) |
| HeightMap | `HeightMapDilation.cs` | Drop cutter: `tip[i,j] = max over footprint of (model[i+dx, j+dy] - dz)`, and the closing `ComputeRemaining` (material left when the tip has been everywhere a map allows) |
| HeightMap | `ReachMap.cs` | Reach floor by vote: the value at ascending rank `ceil(n * percent / 100) - 1` of the `n` values `model - dz` over the footprint cells holding stock, never below the stock floor; the pipeline's tip map. `percent` is `MillingProject.ReachPercent`: 50 is the majority rule (a mixed footprint is entered when at least half of it is stock, so the minority cells are cut on purpose), 100 the drop cutter |
| HeightMap | `HeadClearance.cs` | Head-limit map: `limit[i,j] = max over annulus (cutter radius < d <= head radius) of model - CutterLength`; effective tip = `max(tip, limit)`; head-limited mask |
| HeightMap | `DistanceTransform.cs` | Exact Euclidean distance of every cell to the nearest cell of a mask (separable lower envelope of parabolas), used by the separation region |
| Slicing | `MillingStep.cs` | One level: `Level` (Z) and the mask of tool positions taking part |
| Slicing | `SlicePlan.cs` | The levels (`Steps`), the `Coverage` mask of the surface-following strategy, `LowestLevel` |
| Slicing | `Slicer.cs` | Builds the plan: levels from stock top down by `Stepdown` to the lowest tip value, one mask per level (a tip within `LevelTolerance` above a level counts as on it), coverage = every material cell |
| Slicing | `SeparationRegion.cs` | Cut scope: `Everything` keeps the plan; `Separation` restricts every level mask to the model region plus a terraced trench (one cell next to the level's obstacles, everything cut below, and the positions of the deepest level whose head enters the slab widened by `HeadRadius - CutterRadius + margin`), restricts the coverage to the model region and the innermost trench, and returns the `Standing` map (stock surface over never-cut cells, terrace level over trench cells, NaN elsewhere) |
| Slicing | `MaterialIslands.cs` | Separation scope: 8-connected components of standing stock above the reach floor (never-cut cells and terraces); a component touching the grid border or a no-stock cell is the frame, the others are islands with their material volume; islands below `MinIslandVolume` go back to the level masks, the coverage and NaN standing |
| Toolpath | `ToolpathSegment.cs` | `enum MoveKind { Rapid, Feed, Plunge }` and the segment: `Start`, `End` (`Vector3`), `Kind`, `FeedRate` |
| Toolpath | `Toolpath.cs` | Segment list, `Bounds`, `TotalLength(kind)` |
| Toolpath | `ToolpathStatistics.cs` | Lengths per kind, estimated time from rates, segment counts |
| Toolpath | `ToolpathContext.cs` | Inputs handed to a strategy: tip map, head-limit map, stock map, slice plan, tool, parameters |
| Toolpath | `IToolpathStrategy.cs` | `Id`, `DisplayName`, `Generate(ToolpathContext, IProgress<float>, CancellationToken) -> Toolpath` (the whole program, first plunge to last retract) |
| Toolpath | `StrategyRegistry.cs` | Explicit static list of the two routing strategies; `GetById`, `All`; duplicate id = exception |
| Toolpath | `NodeLattice.cs` | Tool positions a route visits inside a region: the cells on a square lattice spaced by the stepover (first and last grid line always included) plus the outline cells of the region |
| Toolpath | `CaveTree.cs` | Level masks as a forest of caves: 8-connected components per level, each hanging under the component of the level above that contains it (masks are nested because the reach floor is monotone) |
| Toolpath | `RouteWriter.cs` | Node order to segments: cuts follow `SurfacePath` (feeds; a descent steeper than `MaxRampSlope` becomes a feed over the point and a plunge), travels take the polyline or a retract by time, plunge from safe Z at the start, retract at the end |
| Toolpath | `GougeChecker.cs` | Verifies no feed segment goes below the tip map (used by tests and analysis) |
| Toolpath | `ToolpathSimplifier.cs` | Vector output: every run of consecutive feed segments at one rate is reduced by Douglas-Peucker to the vertices needed within `Tolerance`, then a merge pass drops kept vertices whose neighbours' chord still holds; a chord is also rejected when it dips below the effective tip map; rapids, plunges and run end points are untouched |
| Toolpath/Strategies | `ZLayerByLayerStrategy.cs` | Id `z-layer-by-layer` (default). Cave by cave: the nodes of a cave at its level (`NodeLattice` at `Stepover`) in the order `RouteSolver` finds over the material as it stands then, one level down in place, the children before the next sibling, a rise only when a subtree is done |
| Toolpath/Strategies | `ThreeAxisFreedomStrategy.cs` | Id `three-axis-freedom` ("3 axis freedom"). One free route per level of the plan over the coverage cells whose tip lies below the previous level, on the `FinishingStepover` lattice plus every cell where the level map `max(tip, level)` steps by more than `Tolerance`, each at `max(tip, level)`; moves follow the surface polyline over the level map, so no pass cuts deeper than one `Stepdown` and the last visit of a cell is at its tip |
| GCode | `GCodeFormatter.cs` | Invariant number formatting, 3 decimals, trailing zero trimming |

`src/Miller.Solver` (namespace `Miller.Solver`, no references):

| File | Responsibility |
|---|---|
| `RouteGrid.cs` | Flat row-major clearance field (`Floor[j * Width + i]`, NaN = nothing stands there) |
| `RouteProblem.cs` | Nodes as `X`, `Y`, `Z` arrays over a `RouteGrid` |
| `SurfacePath.cs` | The polyline between two points that never dips under a plateau: DDA over the grid lines, every crossing lifted to the highest plateau touching it (two at an edge, four at a corner) and never below the straight line between the ends, rise and descent in place at the ends; returns the vertical travel |
| `RouteCost.cs` | `XySpeedFactor = 3`, `ZSpeedFactor = 1`; exact cost = XY / 3 + climb of the polyline; lower bound = XY / 3 + height difference |
| `RouteBudget.cs` | `MaxEvaluations = 40,000,000` candidate moves per program, shared by all route instances in proportion to their nodes |
| `SpatialBuckets.cs` | Uniform buckets for k-nearest and nearest-unvisited ring searches |
| `RouteSolver.cs` | `Solve(problem, start, budget, allowance, cancellation)`: candidate lists (10 planar-nearest with exact costs), the cheaper of the nearest-neighbour walk and the smooth walk (each step scored with the cheapest next step, turn fine included), then `LocalSearch`; deterministic; `PathCost` = travel + turn fine |
| `LocalSearch.cs` | 2-opt and Or-opt (segments of 1 to 3) on an open path with a fixed first node, don't-look bits, current edge costs and XY lengths kept, the fined status of every node kept current, the exact fine change of every move, free edges bounded from below before an exact trace, a direct-mapped pair-cost cache, segment moves as two or three reversals |
| `TurnFine.cs` | Turn fine: XY turn above 35 degrees fined unless four consecutive nodes form a real arc (one circle within half a cell, same side, both turns below 90 degrees); 5 mm before and after at 0.3 of the speed, overlaps once, route ends clip; `IsFined`, `SlowLength`, `Fine` |
| `FineWindow.cs` | `PathView` (a route as up to four forward or reversed pieces of a base order) and `FineWindow` (the part of the slow length that depends on the changed nodes; stretches between them walked once up to 10 mm and shared by every view) |
| GCode | `IPostProcessor.cs` | `Id`, `DisplayName`, `FileExtension`, `Write(Toolpath, MillingProject, TextWriter)` |
| GCode | `PostProcessorRegistry.cs` | Explicit static list; `GetById`, `All` |
| GCode | `GrblPostProcessor.cs` | Id `grbl`, extension `.nc`. Header comments, `G21 G90 G94 G17`, `S.. M3`, `G0`/`G1` with `F`, `M5`, `M30` |
| Simulation | `StockModel.cs` | Stock `HeightMap` from `StockDefinition` (cylinder: NaN outside the circle), positioned relative to the model per `AxisSetup` |
| Simulation | `MaterialRemover.cs` | Sweeps one segment: samples at most `CellSize / 2` apart; per sample `stock = min(stock, z + dz)` over the footprint; returns the dirty rectangle |
| Simulation | `SimulationClock.cs` | Speed factor clamped to [0.1, 1000]; `Advance(realSeconds) -> simSeconds`; pause; `Seek(simSeconds)` |
| Simulation | `SimulationEngine.cs` | Position along the toolpath (segment index + distance), `Step(simSeconds)`, `SeekTo(length)` (forward, by path length), `RunToEnd()`, `Reset()`, current tool position, progress, `ElapsedSeconds` |
| Simulation | `CollisionDetector.cs` | Head annulus vs current stock, rapid move into material; emits `SimulationEvent` |
| Simulation | `SimulationEvent.cs` | Kind (HeadCollision, RapidIntoMaterial), segment index, position |
| Analysis | `DeviationMap.cs` | `stock - model` per cell where model exists; category per cell (Ok, RestMaterial, Gouge, NoModel) |
| Analysis | `FinalModelAnalyzer.cs` | Builds `DeviationMap` from the final stock; statistics (rest volume, gouge volume, area fractions) |
| Analysis | `UncuttableRegions.cs` | Masks: Overhang (downward-facing surface below the top surface), HeadLimited (from `HeadClearance`), CornerLimited (tip-derived surface above model by more than tolerance) |

### 4.2 src/Miller.Application

| File | Responsibility |
|---|---|
| `Services/ProjectService.cs` | Current `MillingProject`, change notification, new/load/save via `ProjectSerializer` |
| `Services/MeshImportService.cs` | Loads STL files through `StlReader`, validates (non-empty, finite bounds), keeps one `Mesh` per model placement in project order |
| `Services/PipelineService.cs` | mesh -> `AxisSetup` transform -> stock (aligned to `ModelLayout.AnchorBoundsMachine`) -> model map -> tip map -> head limit -> slice plan -> cut scope -> routing strategy -> simplifier -> statistics; progress and cancellation |
| `Services/ExportService.cs` | Toolpath + project -> post-processor -> `.nc` file |
| `Services/SimulationService.cs` | Owns `SimulationEngine`, `SimulationClock`, `MaterialRemover`, `CollisionDetector`; `Advance(realSeconds)`; `SeekTo(fraction)` (forward sweeps in place, backward replays from a fresh stock); exposes snapshot (tool position, dirty rectangle, events) |
| `Services/AnalysisService.cs` | Runs `FinalModelAnalyzer` and `UncuttableRegions` on demand |
| `Services/SettingsService.cs` | User preferences JSON in the per-user application data folder: last folders, window size, last speed factor |
| `Services/PresetService.cs` | Named `MillingPreset`s in one `presets.json` next to the executable (`AppContext.BaseDirectory`); `Load`, `Save` (replace by name, case-insensitive), `Delete`; a corrupt file throws |
| `Services/LogService.cs` | Append-only text log in `logs/miller.log` next to the executable; exception formatting |
| `Validation/ProjectValidator.cs` | Rule list with messages: cutter length > 0, head diameter > cutter diameter, stepover in (0, cutter diameter], stepdown > 0, safe height > 0, cell size in [0.01, 5] mm, feed rates > 0, reach percent in (0, 100], model fits inside the stock box in machine space (`ModelLayout.StockBoundsMachine`) |
| `Validation/ValidationResult.cs` | Errors and warnings with the field name they refer to |
| `Progress/ProgressReport.cs` | Stage name, fraction 0..1, message |

### 4.3 src/Miller.App

| Folder | File | Responsibility |
|---|---|---|
| root | `Program.cs` | Entry point. `AppVersion = "Build_1.0.0"`. CLI: `--version` prints the version and exits; `--export <project.json> <out.nc>` runs the pipeline headless and exits (used to verify the Linux build on a Linux machine). Otherwise starts Avalonia with `UsePlatformDetect()` |
| root | `App.axaml`, `App.axaml.cs` | Fluent theme, includes `Styles/Colors.axaml` and `Styles/Theme.axaml`, composition root (manual construction of services and view models, no DI container) |
| Styles | `Colors.axaml` | The only file with color literals |
| Styles | `Theme.axaml` | Control styles referencing `Colors.axaml` resources |
| Views | `MainWindow.axaml(.cs)` | Menu bar, left settings tabs, central viewport, bottom status bar with progress |
| Views | `MainMenu.axaml` | File (Open STL, Open Project, Save Project, Save Project As, Export NC, Exit), Toolpath (Generate, Cancel), View (Reset Camera; Show Model, Stock, Toolpath, Tool as check items bound two-way to the viewport flags), Simulation (Play, Pause, Stop, Run To End), Help (About) |
| Views | `ToolSettingsView.axaml` | Cutter diameter, cutter length, head diameter, tip type |
| Views | `StockSettingsView.axaml` | Shape, dimensions, placement, margin, fit button |
| Views | `AxisSettingsView.axaml` | Axis mapping, directions, rotations, origin mode |
| Views | `PresetsView.axaml` | First tab: preset list, name box, Save, Load, Delete |
| Views | `ModelsView.axaml` | Model list with add and remove, auto-fit stock alignment per axis (Min, Center, Max), offset and rotation of the selected model, Min, Center and Max per axis |
| Views | `CuttingParametersView.axaml` | Feed, plunge, rapid, spindle, stepover, stepdown, safe height, cell size, direction |
| Views | `StrategySelectionView.axaml` | Routing strategy, cut scope, minimum island volume, reach percent, post-processor, Generate button, statistics |
| Views | `SimulationControlsView.axaml` | Play, pause, stop, step, run-to-end, logarithmic speed slider 0.1 to 1000 with numeric entry, progress bar (a press seeks to that fraction), simulated time, collision counter |
| Views | `AnalysisView.axaml` | Final-model mode toggle, legend (Ok, RestMaterial, Gouge, Overhang, HeadLimited, CornerLimited), statistics |
| Views | `AboutWindow.axaml` | Version, licenses pointer |
| Views | `Viewport3DControl.cs` | `OpenGlControlBase` subclass: init, render, deinit; right drag orbits, wheel drag pans, wheel zooms, double click fits, left press picks a model and drags it in X and Y (ray-plane at the hit height); delegates to `SceneRenderer` |
| Controls | `NumericBox.cs` | `TextBox` subclass with a float `Value`: typed text is kept as typed, valid text is committed per keystroke, invalid text sets a data validation error, the text is rewritten only on an outside `Value` change |
| ViewModels | `ViewModelBase.cs` | `ObservableObject` base with validation helpers |
| ViewModels | `MainWindowViewModel.cs` | Commands for the menu, owns child view models, status text |
| ViewModels | `ToolSettingsViewModel.cs`, `StockSettingsViewModel.cs`, `AxisSettingsViewModel.cs`, `CuttingParametersViewModel.cs`, `StrategySelectionViewModel.cs`, `ModelsViewModel.cs`, `PresetsViewModel.cs`, `SimulationViewModel.cs`, `AnalysisViewModel.cs`, `ViewportViewModel.cs` | One per view; bind to the project through `ProjectService`; validation messages from `ProjectValidator`; `ViewportViewModel` also owns the pick and the drag (`BeginDrag`, `DragTo`, `ModelDragged`), hidden models are not hit |
| Rendering | `GlConstants.cs` | GL enum values not exposed by Avalonia's `GlInterface` |
| Rendering | `GlFunctions.cs` | Delegates obtained via `GlInterface.GetProcAddress` for VAO, buffer and uniform functions missing from `GlInterface` |
| Rendering | `GlShaders.cs` | GLSL sources; version preamble selected from `GlVersion` (`#version 300 es` + precision, or `#version 330 core`) |
| Rendering | `ShaderProgram.cs` | Compile, link, uniform lookup, error text |
| Rendering | `Camera.cs` | Orbit camera: target, yaw, pitch, distance; view and projection matrices; `FitToBounds` |
| Rendering | `MeshRenderer.cs` | Model mesh with flat normals and directional light |
| Rendering | `HeightMapRenderer.cs` | Stock / final model as a grid mesh; partial vertex updates from dirty rectangles; per-cell category coloring |
| Rendering | `ToolpathRenderer.cs` | Line segments colored by `MoveKind`; progress highlight |
| Rendering | `ToolRenderer.cs` | Cutter cylinder and head cylinder at the current tool position |
| Rendering | `AxisTriadRenderer.cs` | Machine axes and stock outline |
| Rendering | `SceneRenderer.cs` | Composes the renderers; single place that issues draw calls |
| Services | `IFileDialogService.cs`, `FileDialogService.cs` | Open/save dialogs via Avalonia `StorageProvider` |
| Services | `ErrorDialogService.cs` | Shows exceptions from services; writes to `LogService` |
| Services | `UiTimer.cs` | 60 Hz `DispatcherTimer` driving `SimulationService.Advance` |
| Services | `GpuPreference.cs` | Registers the executable for the high performance GPU in the user's DirectX graphics preferences when no entry exists (Windows only; the Linux launchers export `DRI_PRIME=1`) |
| Converters | `LogSliderConverter.cs` | Slider position <-> speed factor (logarithmic) |
| Assets | `Icons/miller.ico`, `Icons/miller.png` | Application icon |

### 4.4 tests/Miller.Tests

Mirrors the source tree: `Core/<Folder>/<Type>Tests.cs`,
`Application/<Service>Tests.cs`, `App/<ViewModel>Tests.cs`,
`App/MainWindowHeadlessTests.cs`. Golden G-code files live in
`tests/Miller.Tests/Golden/`. Fixtures: the repository root
`Milling_Heart_V2.STL` plus small meshes generated in test code
(`Fixtures/TestMeshes.cs`).

### 4.5 Other top-level items

| Path | Purpose |
|---|---|
| `build.sh` | The only build script. Restore, build, test, publish the host runtime (`win-x64` on Windows, `linux-x64` on Linux), assemble `dist/`. Runs on Linux and on Git Bash for Windows |
| `src/Miller.Native/` | The native C library of the toolpath generation: `CMakeLists.txt`, `include/miller_native.h` (the exported API), `src/*.c`; built into `src/Miller.Native/build/out` |
| `launchers/Miller.sh` | Copied to `dist/linux-x64/Miller.sh`; `cd` to its own directory and `exec ./Miller "$@"` |
| `scripts/vendor-packages.sh` | One-time, online: downloads every package in `Directory.Packages.props` with dependencies into `third_party/nuget/` |
| `third_party/nuget/` | Vendored `.nupkg` files; the only NuGet source. Git-ignored, filled once per clone by `scripts/vendor-packages.sh` |
| `samples/` | Sample project file for the heart fixture |
| `docs/` | This file and the development guide |

## 5. Data flow

### 5.1 Toolpath pipeline (PipelineService)

Every step from the transform to the statistics runs inside the native library
in one `mn_generate` call (`ToolpathGeneration.Run`); the names below are the
facades that expose the same step on its own.

```
STL file
  -> StlReader ................ Mesh (model coordinates)
  -> AxisSetup.ToMatrix() ..... Mesh (machine coordinates, Z up, origin per OriginMode)
  -> StockModel ............... stock HeightMap (box or cylinder, top = stock top), aligned to the
                                anchor (models before their offsets), so offsets move models inside it
  -> MeshRasterizer ........... model HeightMap (max Z per cell, floor where no model)
  -> ReachMap ................. tip HeightMap: reach floor per position by footprint vote (ReachPercent)
  -> HeadClearance ............ head-limit HeightMap, effective tip = max(tip, limit)
  -> Slicer ................... SlicePlan (levels with masks, coverage)
  -> SeparationRegion ......... plan restricted to the cut scope, Standing map; strategies see
                                effective tip = max(effective tip, standing)
  -> StrategyRegistry.GetById(RoutingStrategyId).Generate(context) -> Toolpath
                                (NodeLattice, CaveTree, RouteSolver, RouteWriter)
  -> ToolpathSimplifier ....... feed runs reduced to vectors within Tolerance
  -> ToolpathStatistics
  -> PostProcessorRegistry.GetById(PostProcessorId).Write(...) -> .nc file
```

### 5.2 Simulation (SimulationService, driven by UiTimer)

```
real dt (seconds) -> SimulationClock (x speed factor) -> sim dt
sim dt -> SimulationEngine.Step: distance = rate(kind) * simDt / 60
       -> for each covered segment part: MaterialRemover.Sweep(stock, segment part)
       -> CollisionDetector.Check(stock, tool position)
       -> snapshot: tool position, dirty rectangle, events
snapshot -> ViewportViewModel -> HeightMapRenderer (partial update), ToolRenderer
```

Processing and rendering are decoupled: all segments covered by one tick are
processed in that tick; the viewport redraws once per frame. At 1000x the work
per tick grows; the code path does not change.

A press on the progress bar calls `SimulationService.SeekTo(fraction)` on a
background task with the clock paused: forward from the current position the
engine sweeps the part in between, backward it replays from a fresh stock clone
(the same sweeps, so stock, events and simulated time equal a run stopped
there). The viewport uploads the stock again afterwards; a simulation that was
playing continues from the new position.

### 5.3 Final model preview (AnalysisService)

```
SimulationEngine.RunToEnd() on a fresh StockModel -> final stock HeightMap
FinalModelAnalyzer: deviation = stock - model (where model exists)
   |dev| <= Tolerance  -> Ok
   dev  >  Tolerance   -> RestMaterial (tool could not reach: corner radius, head limit, cutter length)
   dev  < -Tolerance   -> Gouge (should never happen; indicates a strategy bug)
UncuttableRegions: Overhang (surface hidden from +Z), HeadLimited, CornerLimited
HeightMapRenderer colors cells by category using Colors.axaml resources.
```

## 6. Extension points

### 6.1 Adding a toolpath strategy

1. Add `src/Miller.Core/Toolpath/Strategies/<Name>Strategy.cs` implementing
   `IToolpathStrategy` with a unique lowercase id (for example `adaptive-clearing`);
   it returns the whole program, so it ends with `RouteWriter.Finish()` or the
   equivalent.
2. Add one line to the list in `StrategyRegistry.cs`.
3. Add `tests/Miller.Tests/Core/Toolpath/<Name>StrategyTests.cs` with at least a
   gouge check (`GougeChecker`) and a coverage check.

The UI lists strategies from the registry; no UI change is needed.

### 6.2 Adding a post-processor

Same pattern with `IPostProcessor` and `PostProcessorRegistry.cs`, plus a golden
file test in `tests/Miller.Tests/Golden/`.

### 6.3 Adding a stock shape

`StockDefinition.Shape` enum value plus the mask generation branch in
`StockModel.cs` and the outline in `AxisTriadRenderer.cs`. Validation rules in
`ProjectValidator.cs`.

## 7. Cross-platform build and launchers

- Source is identical for both systems. `build.sh` runs
  `dotnet publish src/Miller.App -c Release -r <rid> --self-contained -p:PublishSingleFile=true`
  for the runtime of the host: `win-x64` on Windows, `linux-x64` on Linux (then it
  copies `launchers/Miller.sh` into `dist/linux-x64/`). The native library is
  compiled by the host's C compiler (MSVC with the static C runtime on Windows, gcc
  on Linux), so a system cannot publish the other one without a cross compiler.
- Build requirements besides the .NET 10 SDK: CMake 3.20 or newer and a C11
  compiler (Visual Studio Build Tools on Windows, `gcc` and `make` on Linux).
- Windows start file: `dist/win-x64/Miller.exe`.
- Linux start file: `dist/linux-x64/Miller.sh` (executable bit set by `build.sh`).
- Root launchers `Miller.cmd` (Windows) and `Miller.sh` (Linux, Git Bash) run the published
  binary from `dist/` with the given arguments and refuse with a hint when it is not built.
  `Miller.cmd` starts the window detached so its console closes; a `--` command runs in the
  console and waits for the result.
- The Linux build also runs on Windows through WSLg; this is not the primary
  path, only a consequence of G1.
- The Linux build is the same `bash build.sh` on a Linux machine with the .NET 10
  SDK installed; packages come from `third_party/nuget` only (`NuGet.config`
  clears all other sources), so the build needs no network once
  `scripts/vendor-packages.sh` has filled the folder. No container is part of
  the project.
- Headless verification of the Linux binary: `./Miller --version` and
  `./Miller --export samples/heart.miller.json out.nc` compared with the golden
  file.

## 8. Placeholder convention

The repository is delivered as an architecture with placeholders and no
implementation. Two placeholder forms exist:

1. Files whose format allows a comment-only body (`.cs`, `.sh`)
   exist under their final name and contain only a header comment:
   purpose, responsibilities, public interface names, dependencies, forbidden
   dependencies, implementing task id. They compile as empty files.
2. Files whose format does not allow a comment-only body (`.axaml`, `.csproj`,
   `.sln`, `.json`, `.props`, `.config`, `.ico`, `.png`, `.nc`) are represented
   by `<final name>.placeholder.md` next to where the final file will be. The
   implementing task deletes the placeholder when it creates the real file.

A placeholder is never partially implemented: the task that owns it replaces the
whole header with the implementation and keeps only comments that add value.

## 9. Numeric and coordinate conventions

- Units: millimetres, degrees, mm/min, rpm. No unit conversion anywhere except
  the G-code `G21` declaration.
- Machine coordinate system: right-handed, Z up, tool moves in +Z to retract.
  Stock top is the highest Z of the stock; `SafeHeight` is the clearance above it,
  so rapid moves run at stock top + `SafeHeight`.
- Heightmap cell `(i, j)` covers world `x in [OriginX + i*CellSize, +CellSize)`,
  `y` likewise; the sample point is the cell center.
- `float.NaN` in a heightmap means "no material here" (outside a cylinder, or
  cut through the floor). `float.NegativeInfinity` is never used.
- Number output uses `CultureInfo.InvariantCulture` everywhere. Number input from
  UI text boxes is parsed with invariant culture as well.
