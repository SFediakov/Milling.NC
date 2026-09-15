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
