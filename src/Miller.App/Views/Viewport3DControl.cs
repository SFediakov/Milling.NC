// PLACEHOLDER - implemented by T-078 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Views
// Purpose: OpenGlControlBase subclass hosting SceneRenderer. All GL calls only in OnOpenGlInit,
//     OnOpenGlRender, OnOpenGlDeinit. Mouse: left drag orbit, right drag pan, wheel zoom, double
//     click fit.
// Public interface (names only): sealed class Viewport3DControl : OpenGlControlBase {
//     ViewportViewModel? ViewModel; override void OnOpenGlInit(GlInterface gl); override void
//     OnOpenGlRender(GlInterface gl, int fb); override void OnOpenGlDeinit(GlInterface gl) }
// Depends on: SceneRenderer, Camera, ViewportViewModel
// Must not depend on: GL calls outside the three overrides
