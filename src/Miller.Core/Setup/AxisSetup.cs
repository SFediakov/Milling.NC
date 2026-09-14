// PLACEHOLDER - implemented by T-018 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Setup
// Purpose: Maps model axes to machine axes with direction flips, rotations and the origin mode;
//     produces the single transform applied to the mesh.
// Public interface (names only): enum ModelAxis { X, Y, Z }; enum OriginMode {
//     StockCornerMinXYMinZ, StockCornerMinXYTopZ, StockCenterTopZ, Custom }; sealed class AxisSetup
//     { ModelAxis MapX, MapY, MapZ; bool FlipX, FlipY, FlipZ; float RotationX, RotationY,
//     RotationZ; OriginMode OriginMode; Vector3 CustomOffset; Matrix4x4 ToMatrix(BoundingBox
//     modelBounds, StockDefinition stock); static AxisSetup Default() }
// Depends on: BoundingBox, StockDefinition
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
