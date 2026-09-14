// PLACEHOLDER - implemented by T-082 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Rendering
// Purpose: Stock or final model as a grid mesh (NaN cells omitted through indices); normals from
//     neighbors; per-vertex color from a single color or a category array; partial updates by dirty
//     rectangle.
// Public interface (names only): sealed class HeightMapRenderer : IDisposable { const int
//     MaxCellsForInteractiveFrame; void Upload(GlInterface gl, GlFunctions fn, HeightMap map,
//     Vector4 color); void Update(GlInterface gl, GlFunctions fn, HeightMap map, DirtyRect dirty);
//     void SetCategories(GlInterface gl, GlFunctions fn, CellCategory[] categories,
//     IReadOnlyDictionary<CellCategory, Vector4> colors); void Draw(GlInterface gl, GlFunctions fn,
//     ShaderProgram lit) }
// Depends on: Avalonia.OpenGL GlInterface, GlFunctions, GlConstants
// Must not depend on: a second OpenGL binding package; platform conditionals
