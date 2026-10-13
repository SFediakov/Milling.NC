# Vocabulary

Interpretations of requests that the user clarified during a session.

| Initial wording | Proper interpretation |
|---|---|
| "all process of tool path generation should be executed from one built C++ dll" | The toolpath generation runs from one native library written in C (C11), not C++: `src/Miller.Native`, `miller_native.dll` on Windows and `libmiller_native.so` on Linux (T-134). |
| "routes directly above the stock top with the smallest possible gap, it does not consider safe height above the stock top" | No move may come closer to uncut material at the stock top than SafeHeight, including a single point of a cutting route (a diagonal step past the corner of an uncut cell, or of a model or standing stock face flush with the top): such a point takes stock top + SafeHeight, never the top plane itself. |
| "provide after the end % of unreached cells" (Backwards) | "At the end" is the summary window that opens when a generation finishes, not the statistics panel: it names the cells left uncut that the plan meant to cut (count, area, volume, highest) and, in Backwards, the unreached share (T-173). |
| "independent point which is a global XYZ in the 3D model and both a model and stock would be able to moved in reference to this" (machine zero) | The global point is the origin of the global frame itself, the one 0 of the axes (mapping and rotations turn about it), the model offsets and the explicit stock corner; it takes no offset of its own, and choosing it keeps the stock where it stands against the models (T-189 to T-191). |
