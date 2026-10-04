# Vocabulary

Interpretations of requests that the user clarified during a session.

| Initial wording | Proper interpretation |
|---|---|
| "all process of tool path generation should be executed from one built C++ dll" | The toolpath generation runs from one native library written in C (C11), not C++: `src/Miller.Native`, `miller_native.dll` on Windows and `libmiller_native.so` on Linux (T-134). |
| "routes directly above the stock top with the smallest possible gap, it does not consider safe height above the stock top" | No move may come closer to uncut material at the stock top than SafeHeight, including a single point of a cutting route (a diagonal step past the corner of an uncut cell, or of a model or standing stock face flush with the top): such a point takes stock top + SafeHeight, never the top plane itself. |
