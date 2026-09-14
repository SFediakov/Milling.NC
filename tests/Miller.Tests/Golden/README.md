# Golden files

Expected outputs compared byte for byte by tests.

| File | Produced by | Compared by |
|---|---|---|
| `square_grbl.nc` | T-053, written once from the implementation and reviewed line by line against the template in `docs/DEVELOPMENT_GUIDE.md` 6.5 | `Core/GCode/GrblPostProcessorTests.cs` |
| `heart_grbl.nc` | T-056, `Miller --export samples/heart.miller.json` on Windows | `App/EndToEndTests.cs` and the Docker diff |

A golden file changes only through a task whose text says that it changes, and the diff is reviewed by hand.
The version comment line is replaced before comparison so a version bump does not invalidate the files.
