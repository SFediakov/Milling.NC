// PLACEHOLDER - implemented by T-078 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Rendering
// Purpose: Composes the renderers; the single place that clears the frame and issues draw calls;
//     reads colors from Application resources once per upload.
// Public interface (names only): sealed class SceneRenderer : IDisposable {
//     SceneRenderer(GlInterface gl, GlVersion version); Camera Camera; void Sync(ViewportViewModel
//     vm); void Render(GlInterface gl, int width, int height) }
// Depends on: Avalonia.OpenGL GlInterface, GlFunctions, GlConstants
// Must not depend on: a second OpenGL binding package; platform conditionals
