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
  folder.
- `Avalonia.Diagnostics` is not referenced although `docs/ARCHITECTURE.md` 2.2 lists it at
  12.1.2. Avalonia 12 removed the package (its last version is 11.3.x); the replacement
  `AvaloniaUI.DiagnosticsSupport` is a different package outside the allowed list. It was
  Debug-only developer tooling with no shipped feature behind it, so it is dropped from
  `Directory.Packages.props`, `Miller.App.csproj` and `third_party/nuget/`. Adding
  `AvaloniaUI.DiagnosticsSupport` later is a one-line change in T-002 and T-006.

## Placeholder convention (architecture delivered without implementation)

- `.cs`, `.sh`, `.editorconfig` placeholders are comment-only
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
- Rendering check: tests/Miller.Tests/App/RenderCaptureTests.cs renders every settings tab
  through the headless Skia platform and writes out/ui-captures/tab-*.png; inspect those files
  after a UI task. Desktop screen captures are unreliable on this machine (200 percent DPI
  scaling shifts window and click coordinates). The GL viewport does not render headless.
- Compiled bindings cannot bind to constants; use x:Static for them in XAML.
- Window.Width and Height are NaN until something sets them; persist ClientSize, and only when
  it is finite and positive.
- Files written by the other agent use CRLF; text patches must detect and keep the line ending.
- Avalonia 12 GlInterface already wraps buffers, vertex arrays, shaders, programs, uniforms
  (Uniform1f, Uniform1i, UniformMatrix4fv), attribute pointers, draw calls, clear, viewport,
  depth function and mask. Missing and loaded through GetProcAddress in GlFunctions:
  glBufferSubData, glUniform3f, glUniform4f, glLineWidth, glCullFace, glPolygonOffset,
  glDisableVertexAttribArray. GlConsts lacks GL_LINES, GL_LINE_STRIP, GL_LEQUAL, GL_BLEND,
  GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_POLYGON_OFFSET_FILL, GL_DYNAMIC_DRAW; those live in
  GlConstants. Probe the assembly strings before assuming a wrapper exists.
- Menu shortcuts are window-level KeyBindings in MainWindow.axaml; the MenuItem keeps only
  InputGesture for display. MenuItem.HotKey looked right but never fired from the window: an item in
  a closed submenu is not in the visual tree, reports IsEffectivelyEnabled false, and Avalonia's
  hotkey wrapper refuses it, so Ctrl+O, Ctrl+S, Ctrl+E and F5 only worked while their menu was
  open. A headless test presses the gestures and asserts the commands ran. Space stays reserved for
  the viewport because a window-level binding fires while typing in a text box; Ctrl+letter and
  function keys do not collide with text editing.
- Key gestures for digits are written Ctrl+D1, not Ctrl+1: KeyGesture parses a bare digit as the
  numeric value of the Key enum (1 is Cancel, 2 is Back).
- Progress<T> delivers callbacks through the synchronization context captured at construction;
  without one (plain tests) they run on the thread pool and can land after the awaited task,
  which made a status assertion flaky on Linux. The view model takes a progress factory: the
  application passes Progress<T> on the UI thread, tests pass a synchronous progress.
- Desktop capture of the GL viewport on this machine: the display is 3840 x 2160 at 250 percent.
  In a DPI-unaware PowerShell, MoveWindow and GetWindowRect use virtual pixels (1536 x 864 screen)
  while System.Drawing CopyFromScreen reads physical pixels, so capture the whole 3840 x 2160
  screen after moving the window to the origin; a partial region silently cuts the viewport.
  Several rendering "failures" were only this.
- GL problems are visible: SceneRenderer.Render returns glGetError and Viewport3DControl reports
  it (and init exceptions) through ViewportViewModel.GlError into the log and the status bar.
- Camera.FitToBounds must use the narrower of the vertical and horizontal half-angles, or a tall
  viewport clips the sides of the model.
- ProjectService.MarkDirty raises ProjectChanged on every call, not only on the clean-to-dirty
  transition; the viewport follows each stock or tool edit through that event. The main view model
  transforms and uploads the mesh again only when the mesh instance or the axis matrix changed, so
  typing in a panel does not refit the camera.
- Renderer geometry (grid indices, walls, tool cylinders, toolpath split) is built by static methods
  and covered by tests/Miller.Tests/App/RendererGeometryTests.cs without a GL context; desktop
  captures are the last resort, and each one costs a run of the application.
- Desktop automation on this machine: SendKeys and posted key or wheel messages do not reach the
  Avalonia window reliably; mouse_event clicks do. Drive the UI through menu clicks when a capture
  is unavoidable and prefer headless tests (KeyPressQwerty) for keyboard behaviour.
- The interactive cell limit is ProjectValidator.MaxInteractiveCells in the Application layer;
  HeightMapRenderer.MaxCellsForInteractiveFrame only mirrors it. The guide names the renderer as
  the owner, but Application cannot reference App, so the constant lives one layer down.
- The GL viewport has not been exercised on Linux (no Linux desktop here); T-086 rests on the
  Windows captures plus the shared code path. Frame budget is guarded on the CPU side by the build
  timing test in CameraTests, not by a GPU measurement.
- SimulationEngine takes no CuttingParameters: every ToolpathSegment carries its own rate (rapids
  the rapid rate), so the engine covers distance = segment rate * seconds / 60 from the segment.
  The engine raises Sampled at the end of each covered part and along rapids at max(cell size,
  cutter radius); the service runs CollisionDetector there and keeps one event per segment and
  kind. Checking the head annulus at every sweep sample would cost more than the sweep itself.
- The simulation cuts a clone of the pipeline stock; Load and Stop replace the instance, so the
  main view model re-uploads Viewport.StockMap after both, and per-frame changes go through
  ViewportViewModel.MarkStockDirty into HeightMapRenderer.Update (reference-equal map required).
- A simulation test fixture must use a tool that fits the gaps: a 6 mm cutter around an 8 mm box
  in a 12 mm stock leaves nothing reachable, the plan degenerates to one finishing pass on the
  stock top and the tool cuts air for the whole path.
- SimulationService.RunToEnd takes about 20 s for the heart with the default stock (79 m of feed
  at 0.1 mm sample spacing), so the main view model runs it in Task.Run with IsBusy set; the
  service pauses the clock before sweeping so the 60 Hz timer cannot step the engine concurrently.
- The first simulation of the heart found 1404 head collisions: HeadClearance limits the tip by the
  model, but the cutter leaves a strip of stock next to every wall (cells closer than the cutter
  radius to the wall keep the level of the last pass that reached them) and the head hits that
  strip. Recorded as T-090a in the guide; until it is done the collision list is the warning.
- Desktop capture: PrintWindow(PW_RENDERFULLCONTENT) on the app window in a per-monitor DPI aware
  PowerShell renders the app itself even when another window covers it (a full screen copy showed
  VS Code once). Menus opened by clicks work for View and Simulation; the first menu after start
  did not open, so generation is started by the Strategy tab button. RunToEnd blocks the UI, so a
  capture right after it shows the previous frame.
- Docker is not part of this project: the root CLAUDE.md sentence about wiring packages to a
  docker container was copied from another project (user statement). Dockerfile, .dockerignore,
  third_party/debian and scripts/vendor-debian.sh were removed; the offline build is
  third_party/nuget plus build.sh on either system, and Linux checks run on a Linux machine.
- `Miller.cmd` in the root is the one `.cmd` file (user request: a launcher in the root folder).
  The bash-only rule of the guide covers build and tool scripts; a Windows user double-clicks a
  `.cmd`, not a `.sh`. `Miller.sh` next to it serves Linux and Git Bash. Both only start
  `dist/`; they do not build.
- Head collisions had two causes, not one: the head limit ignored rest material, and raster rows
  skip the row nearest a wall (row step equals the cutter radius), so a strip beside every wall
  was never cut at all. The fix pairs a profile pass per level (layer-complete strategy, now the
  default) with a head limit from the closing of the tip map rounded UP to the roughing level the
  material stands at until its pass (Slicer.CeilToLevel); the pure closing alone still collided
  because the strip stays one level higher while the tool cuts the level below next to it.
- ToolpathLinker orders the passes of each group by nearest neighbour; groups (one per level) never
  interleave, one-way milling forbids reversing passes, closed loops keep their direction and are
  only rotated. Tests on strategies must not assume plunge-per-loop or row order any more.
- A Control that draws nothing is invisible to hit testing; Viewport3DControl fills its bounds with
  the transparent brush under the GL surface so pointer events reach it, also in headless tests.
  Headless drags need the button in the MouseMove modifiers, and Avalonia's click counter ignores
  the button, so the double-click fit checks the left button itself.
- The mouse harness (SetCursorPos, mouse_event) cannot produce drags the app sees; camera input is
  verified by ViewportInputTests on the headless platform instead.
- Models and placements live in two lists (MeshImportService.Meshes, MillingProject.Models) that
  are updated one after the other; every consumer checks MeshImportService.Matches(project) and
  treats a mismatch as "no scene" instead of throwing inside a change event. Add the placement
  before importing (drop it when the import fails), remove the placement before the mesh.
- The stock is anchored to the union of the models before their offsets (ModelLayout.AnchorBounds);
  it used to follow the union of the placed models, which made every offset of a lone model a
  no-op (the stock moved along) and turned alignment into a fixed-point iteration. Every consumer
  of the stock box (pipeline, viewport outline, validator) must read it from ModelLayout
  (AnchorBoundsMachine, StockBoundsMachine), never recompute it from model bounds.
- Legacy project files: a nullable StlPath is read for migration and ignored when null on write, so
  the serializer's unknown-member rule still holds; the schema version is rewritten to 2 on load.
- The end-to-end golden is read from the test output folder, which is refreshed by a build of the
  test project; regenerate the golden, then build tests before running the comparison.
- The simulation commands live once, in SimulationViewModel; the menu binds to
  SimulationPanel.* so menu and panel share the enable rules (play only when loaded, not playing,
  not finished; pause only while playing). The main view model only forwards status text and
  reloads the panel after Load and Unload.
- Space is handled by Viewport3DControl.OnKeyDown (focused viewport only) and raised as
  PlayPauseRequested; a window-level binding would fire while typing in a text box.
- The speed slider binds through LogSliderConverter (four decades over one travel); the text box
  parses invariant, clamps through the service and shows the clamped value with a message, and
  every accepted value is written to the settings file at once.
- UncuttableRegions.CornerLimited compares the model with the closing of the effective tip
  (the material the cutter leaves), not with the effective tip as guide 6.3 wrote: the tip map is
  high beside every wall although the cutter edge reaches the wall, so the older formula would
  flag reachable floor next to walls. Head-limited cells take precedence over corner-limited.
- An overhang is any downward face above the floor that is hidden from +Z; a part whose bottom
  floats above the stock bottom counts as overhang under its whole footprint. Tests that want no
  overhang must put the part on the floor (stock height equal to the part height).
- The analysis runs its own SimulationEngine on a stock clone (AnalysisService) and never touches
  the interactive simulation; RunToEnd(CancellationToken) checks the token once per segment.
  xUnit's analyzer wants TestContext.Current.CancellationToken wherever a token overload exists.
- The final model view and the simulation compete for the viewport stock map: playing switches the
  analysis view off (SimulationViewModel.PlaybackStarted), and the analysis returns the simulation
  stock when its view is switched off, so the dirty-rectangle updates always target the map that
  the engine mutates.
- Miller.exe is a GUI-subsystem binary: cmd waits for it inside a batch file, PowerShell's `&`
  does not (no output, no exit code), so launcher behaviour is verified from a batch probe, not
  from PowerShell. Inside a parenthesised cmd block `%ERRORLEVEL%` expands before the block runs;
  `exit /b` without a code passes the app's code through. `start "" "%APP%" %*` detaches the
  window so the console closes; a first argument starting with `--` keeps the console and waits.
- Before a launcher check, list the running Miller processes: the check closes the instances it
  finds from dist, and one was already open before the check ran.
- Windows selects the GPU per executable path from the user's DirectX graphics preferences
  (the key Settings > Display > Graphics writes). GpuPreference writes the high performance entry
  only when none exists, because an existing entry is the user's own choice, and the choice
  applies from the next process start. Registry tests use a root of their own
  (`Software\MillerTests`) deleted as a tree; deleting only the value leaves empty keys behind.
- A python patch script in a heredoc breaks twice on backslashes: the tool collapses doubled ones
  and a Windows path such as `\Users` then turns into a unicode escape error. Scripts that carry
  backslashes go to a file through the Write tool and use raw strings.
- The vector output lives in one place, ToolpathSimplifier, run by the pipeline after linking; the
  strategies still emit cell-by-cell chains and their tests compare vertex sets with marching
  squares, so simplifying inside a strategy or the linker would break those tests for no gain.
  Douglas-Peucker alone is not enough on a plateau grid: a chord within the tolerance can still
  cross a wall cell, so every accepted chord also passes GougeChecker.IsClear.
- Raster finishing rows keep the cell centers only at both ends; the interior points are the cell
  boundaries at the higher tip. The old center-plus-boundary staircase was never collinear on a
  slope and left a sawtooth; the boundary polyline lifts the tool by slope x cell / 2 instead.
- The roughing mask must use the same slack as CeilToLevel (Slicer.LevelTolerance): a rasterized
  30 top reads 30.000002, the head limit from it 18.000002, and an exact "tip <= level" left that
  cell out of the level-18 mask while the head limit assumed it stood at 18. The head then hit it
  from the level below (132 events on a 30 mm stock with a 12 mm cutter, in both cut scopes).
- The separation region must not feed its standing map back into the head-limit iteration: the
  limit from the standing outer stock raises the effective tip beside the trench, that widens the
  model region (tip above the floor), the trench moves outward, and after eight rounds the whole
  stock is model region. The terraces guarantee head clearance by construction; the standing map
  only joins the tip map the strategies stay above.
- Terrace widening must start from every tool position of the deeper level (the whole mask, model
  region included), not from the trench cells alone: the tool also works on the model-region ring
  the head limit leaves beside a tall part, and the outer wall above must clear its head too.
- The heart sample pins raster-roughing, whose rows skip anything thinner than a stepover; the 47
  head events it reports are that strategy's known limitation, and thin separation trenches need
  the profile pass of layer-complete (zero events on the heart in both scopes).
- Git with core.autocrlf=true rewrites a stashed and restored file with CRLF; a `python -c` patch
  that searches for LF text then finds nothing. Normalise the file back to LF before patching, or
  read it with newline='' and match on the ending it has.
- Two-model alignment tests written for the union-following stock (35 mm start of the first box,
  fixed-point centres) are not wrong values of the anchored rule: the anchor puts both boxes at
  0..10 before their offsets, so the stock middle is 50 whatever the offsets are.
- Seeking backward is Stop plus a replay: the engine only covers forward, the service swaps in a
  fresh stock clone first, so the view model re-uploads the stock map after every seek exactly as
  it does after Stop. The clock is paused before the background sweep and gets the engine's
  elapsed time afterwards; resume only when the seek did not land on the end.
- The plateau model judges a point on a grid line by whichever neighbouring cell CellOf (floor)
  returns, and a corner point by the +x, +y cell. A surface polyline that lifts a crossing to the
  two cells it separates still gouges at a corner crossing: the DDA steps diagonally from cell
  (i, j) to (i + 1, j + 1) and the corner point is judged by that cell or one of the two skipped
  ones. Every crossing is lifted to all cells touching the point (two at an edge, four at a
  corner) with a small epsilon, so the checker and the polyline agree on every sample.
- Douglas-Peucker splits at the farthest vertex: on a long collinear stretch whose lead-in and
  lead-out deviate, every interior vertex is at the same distance and float noise picks one in
  the middle, which leaves two chords for one line. A merge pass over the kept vertices (drop a
  vertex when its neighbours' chord holds every vertex between and is clear) restores one chord.
- A majority reach map with a ball tip votes with model - dz per cell: over a flat top the edge
  cells (large dz) outvote the center and the floor lands one dz below the top, and beside a
  floor the edge values fall below the stock floor. The map is clamped at the floor; the dimple is
  a property of the confirmed rule, not a bug, and is documented in the guide.
- The route solver's local search needs a lower bound before any exact trace: with a rugged
  surface the bound is loose and every candidate move traces the free edge, which held
  throughput at about 3 M evaluations per second; a direct-mapped pair-cost cache doubled it
  because the same free edges are examined again and again while the search moves around them.
  On flat levels the bound is exact and the search runs at about 20 M per second.
- A golden .nc regenerated after `dotnet build` is not what `dotnet test --no-build` compares
  against: the test reads the copy in the output directory (CopyToOutputDirectory), so build the
  test project again after regenerating a golden file or the mismatch looks like nondeterminism.
- The reach map's quickselect over 113 tops per position runs the 300 x 300 heart grid in 0.12 s;
  Span.Sort per position took 0.4 s and, under parallel test load, more than the 2 s the timing
  test allows.
- Settings panels only see validator messages whose field starts with their prefix ("Tool.",
  "Parameters.", "Strategy."), so a root-level project value edited on the Strategy tab must be
  reported as "Strategy.<Name>" or its error never reaches the panel.
- Under the majority reach map the tool axis stays inside a hole and reaches its wall lines; the
  cut-back into the walls happens through the footprint, not by moving the axis outward. An
  island inside a 20 x 20 hole is therefore the hole minus the one-cell trench band, not the hole
  widened by the cutter radius.
- A `Configuration` default in `Directory.Build.props` does not reach `dotnet build Miller.sln`:
  the solution passes its own default (`Debug` whenever the .sln lists one) as a global property
  that outranks every project-level default. `Miller.sln` therefore lists `Release` only; the
  props default covers project-level commands (`dotnet run --project`, `dotnet build <csproj>`).
- The native `.pdb` files that SkiaSharp and HarfBuzzSharp ship under `runtimes/<rid>/native`
  are native assets, not debug symbols: `CopyDebugSymbolFilesFromPackages` never sees them and
  `DebugType=None` does not remove them. They leave only by removing the items from
  `NativeCopyLocalItems` and `RuntimeTargetsCopyLocalItems` after `ResolvePackageAssets`.
- An MSBuild `Condition` cannot hold a property function whose quoted argument contains `;`
  (MSB4090 at the `;`). Compute the value into item metadata in a first `ItemGroup`, then
  condition the `Remove` on that metadata.
- `git filter-branch -- --all` rewrites `refs/stash` as well and the rewritten entry can no
  longer be popped ("not a stash-like commit"); the untouched entry is still `stash@{1}` and
  `git stash apply` restores it. Rewrite `-- main` or pop the stash before the rewrite.
- A MenuItem with ToggleType CheckBox toggles IsChecked in DefaultMenuInteractionHandler.Click before
  the click reaches its Command, so a toggle command on the same item flips the flag twice. Bind
  IsChecked two-way and keep the toggle commands on the window key bindings only.
- A TextBox bound to a float rewrites the typed text on every keystroke: the view model raises
  PropertyChanged after the edit and the binding writes the formatted value back ("0.00" became
  "0"). NumericBox binds a float Value instead; the property system drops an equal float, so the
  echo never reaches the text, and invalid text only sets a data validation error.
- ReachMap's rank ceil(n * percent / 100) - 1 needs a small slack before the ceiling: 4 * 50 / 100
  is exact, but a product such as 3 * 33.3 / 100 can land one ulp above an integer.
- A strategy that visits cells level by level must skip a cell whose tip lies within
  Slicer.LevelTolerance below the previous level, or float noise gives it a second visit for a
  1e-4 mm cut; the first level compares against StockTop, so cells at the stock top are never nodes.
- Under Git Bash a pipe (`Miller.exe --export ... | tail`) waits for the GUI-subsystem binary; the
  export finished before the next command, unlike PowerShell's `&`.
- A list box must not get a new ItemsSource instance on every project edit. ModelsViewModel published
  `Names` as a fresh list from every reload; Avalonia's ListBox resets its SelectionModel on an
  ItemsSource swap, the two-way SelectedIndex binding wrote -1 into the view model, the placement
  fields bound to HasSelection were disabled for an instant, and Avalonia moves the keyboard focus
  away from a focused control that becomes disabled. Typing "-12.5" therefore stopped at "-1" (the
  first valid keystroke). Collections shown in a list are cached and republished only when their
  content differs (SequenceEqual); a value edit must never reach `OnPropertyChanged(nameof(Names))`.
  Focus loss of this kind is invisible to a test that types the whole text in one KeyTextInput;
  CoordinateEntryTests types one character per call and asserts IsFocused after each.
- NumericBox distinguishes incomplete from invalid text: a sign or decimal-point prefix ("-", "+",
  ".", "-.", "+.") clears the error and leaves Value alone, so the first keystroke of a negative
  number is not shown as a mistake; "" and "abc" stay errors as T-128 requires.
