// PLACEHOLDER - implemented by T-075 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Rendering
// Purpose: Delegates resolved with GlInterface.GetProcAddress for the functions GlInterface lacks
//     (VAO, BufferSubData, UniformMatrix4fv, Uniform3f, ...). A zero address throws.
// Public interface (names only): sealed class GlFunctions { GlFunctions(GlInterface gl);
//     GenVertexArrays; BindVertexArray; DeleteVertexArrays; BufferSubData; UniformMatrix4fv;
//     Uniform3f; Uniform4f; VertexAttribPointer; EnableVertexAttribArray }
// Depends on: Avalonia.OpenGL GlInterface, GlFunctions, GlConstants
// Must not depend on: a second OpenGL binding package; platform conditionals
