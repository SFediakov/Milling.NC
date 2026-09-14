// PLACEHOLDER - implemented by T-083 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Rendering
// Purpose: Cutter cylinder (diameter, length, flat disc or ball hemisphere) and head cylinder (head
//     diameter, fixed display length) drawn at the tool position.
// Public interface (names only): sealed class ToolRenderer : IDisposable { void Build(GlInterface
//     gl, GlFunctions fn, ToolDefinition tool, Vector4 cutterColor, Vector4 headColor); void
//     Draw(GlInterface gl, GlFunctions fn, ShaderProgram lit, Vector3 tipPosition) }
// Depends on: Avalonia.OpenGL GlInterface, GlFunctions, GlConstants
// Must not depend on: a second OpenGL binding package; platform conditionals
