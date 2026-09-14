// PLACEHOLDER - implemented by T-016 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the implementation.
// Namespace: Miller.Core.Io
// Purpose: Entry point for STL import. Detects binary by the size rule first (the header text
//     'solid' is not a format indicator), then ASCII.
// Public interface (names only): static class StlReader { static (Mesh Mesh, StlImportReport
//     Report) Read(string path); static (Mesh, StlImportReport) Read(byte[] data, string
//     pathForReport) }
// Depends on: StlBinaryParser, StlAsciiParser, StlImportReport
// Must not depend on: Avalonia, System.IO file dialogs, threads, timers
