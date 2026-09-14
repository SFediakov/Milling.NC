// PLACEHOLDER - implemented by T-014 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Io
// Purpose: Binary STL: 80-byte header, uint32 count, 50-byte little-endian records. Validates
//     fileLength == 84 + 50 * count.
// Public interface (names only): static class StlBinaryParser { static bool
//     MatchesSizeRule(ReadOnlySpan<byte> data); static Mesh Parse(ReadOnlySpan<byte> data) }
// Depends on: Mesh, Triangle
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
