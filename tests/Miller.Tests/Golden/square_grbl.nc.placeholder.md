# Placeholder for `square_grbl.nc`

Implemented by T-053 (docs/DEVELOPMENT_GUIDE.md). Create `tests/Miller.Tests/Golden/square_grbl.nc` and delete this file in the same commit.

Purpose: Golden G-code for a hand-checkable toolpath.

Required content: Toolpath: start at safe height 5 above (0,0); plunge to Z -1 at (0,0); feed (10,0), (10,10), (0,10), (0,0); retract. Default tool and stock in the header; Grbl template of docs/DEVELOPMENT_GUIDE.md 6.5.
