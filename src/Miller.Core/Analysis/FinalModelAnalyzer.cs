// PLACEHOLDER - implemented by T-096 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Analysis
// Purpose: Classifies every cell: |dev| <= tol Ok, dev > tol RestMaterial, dev < -tol Gouge, floor
//     cells NoModel; areas and volumes per category.
// Public interface (names only): sealed record AnalysisResult(DeviationMap Map, int OkCells, int
//     RestCells, int GougeCells, float RestVolume, float GougeVolume, float CellArea); static class
//     FinalModelAnalyzer { static AnalysisResult Analyze(HeightMap finalStock, HeightMap model,
//     float floor, float tolerance) }
// Depends on: DeviationMap, HeightMap
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
