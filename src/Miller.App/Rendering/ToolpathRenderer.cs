// PLACEHOLDER - implemented by T-081 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Rendering
// Purpose: Toolpath as lines colored by MoveKind; draws the done part and the remaining part with
//     an index split at ToolpathProgressIndex.
// Public interface (names only): sealed class ToolpathRenderer : IDisposable { void
//     Upload(GlInterface gl, GlFunctions fn, Toolpath toolpath, IReadOnlyDictionary<MoveKind,
//     Vector4> colors); void Draw(GlInterface gl, GlFunctions fn, ShaderProgram lines, int
//     progressIndex) }
// Depends on: Avalonia.OpenGL GlInterface, GlFunctions, GlConstants
// Must not depend on: a second OpenGL binding package; platform conditionals
