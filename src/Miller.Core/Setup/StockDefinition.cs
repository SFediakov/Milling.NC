// PLACEHOLDER - implemented by T-017 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Setup
// Purpose: Raw material block: box (cubic or rectangular) or cylinder, with placement relative to
//     the model.
// Public interface (names only): enum StockShape { Box, Cylinder }; enum StockPlacement {
//     AutoFitWithMargin, Explicit }; sealed class StockDefinition { StockShape Shape; float SizeX;
//     float SizeY; float SizeZ; float Diameter; float Height; StockPlacement Placement; float
//     Margin; Vector3 ExplicitOrigin; static StockDefinition Default() }
// Depends on: none
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
