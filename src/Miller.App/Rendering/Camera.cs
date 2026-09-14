// PLACEHOLDER - implemented by T-077 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.App.Rendering
// Purpose: Orbit camera: target, yaw, pitch (clamped), distance (clamped), aspect; view and
//     projection matrices; orbit, pan, zoom, fit to bounds.
// Public interface (names only): sealed class Camera { Vector3 Target; float Yaw; float Pitch;
//     float Distance; float Aspect; const float MinPitch, MaxPitch, MinDistance, MaxDistance;
//     Matrix4x4 View; Matrix4x4 Projection; void Orbit(float dx, float dy); void Pan(float dx,
//     float dy); void Zoom(float factor); void FitToBounds(BoundingBox bounds) }
// Depends on: Avalonia.OpenGL GlInterface, GlFunctions, GlConstants
// Must not depend on: a second OpenGL binding package; platform conditionals
