// PLACEHOLDER - implemented by T-060 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Application.Services
// Purpose: Owns the current MillingProject, its file path and the dirty flag; raises
//     ProjectChanged.
// Public interface (names only): sealed class ProjectService { MillingProject Current; string?
//     Path; bool IsDirty; event EventHandler? ProjectChanged; void New(); void Load(string path);
//     void Save(); void SaveAs(string path); void MarkDirty() }
// Depends on: MillingProject, ProjectSerializer
// Must not depend on: Avalonia and any UI type; Miller.App
