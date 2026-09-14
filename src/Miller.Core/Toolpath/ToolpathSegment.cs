// PLACEHOLDER - implemented by T-031 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Toolpath
// Purpose: Straight tool movement between two tip positions, plus the MoveKind enum that decides the feed
//     rate used and the G-code word (G0 for Rapid, G1 otherwise).
// Public interface (names only): enum MoveKind { Rapid, Feed, Plunge }; readonly record struct
//     ToolpathSegment(Vector3 Start, Vector3 End, MoveKind Kind, float FeedRate) { float Length; Vector3 Direction }
// Depends on: none
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
