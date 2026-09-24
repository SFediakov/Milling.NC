# Vocabulary

Interpretations of requests that the user clarified during a session.

| Initial wording | Proper interpretation |
|---|---|
| "all process of tool path generation should be executed from one built C++ dll" | The toolpath generation runs from one native library written in C (C11), not C++: `src/Miller.Native`, `miller_native.dll` on Windows and `libmiller_native.so` on Linux (T-134). |
