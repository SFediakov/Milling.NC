namespace Miller.Core.Setup;

// The rows of the finishing passes (T-222), moved by k / (passes + 1) of the stepover for pass k (half of
// it for one pass): parallel to the roughing rows, across them, or along the 45 degree diagonal. The
// numbers are the native MN_FINISHING_* values.
public enum FinishingPattern
{
    ShiftedRows = 0,
    CrossRows = 1,
    DiagonalRows = 2,
}
