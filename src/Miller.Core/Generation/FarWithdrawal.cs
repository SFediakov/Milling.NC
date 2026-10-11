using Miller.Core.Native;

namespace Miller.Core.Generation;

// The far stepdown withdrawal of the generation passes (T-164, native mn_far_withdraw): every allowed
// position within the radius of a collision place leaves the far stepdown; the passes use
// MN_FAR_WITHDRAW_RADIUS, 1 mm. Returns how many allowed positions it cleared.
public static class FarWithdrawal
{
    public static unsafe int Withdraw(bool[,] places, float cellSize, float radius, bool[,] allowed)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(allowed);
        var width = places.GetLength(0);
        var height = places.GetLength(1);
        if (allowed.GetLength(0) != width || allowed.GetLength(1) != height)
        {
            throw new ArgumentException("The allowed mask must have the size of the places.", nameof(allowed));
        }

        var marked = CoreNative.Bytes(places);
        var bytes = CoreNative.Bytes(allowed);
        int withdrawn;
        fixed (byte* p = marked, a = bytes)
        {
            withdrawn = CoreNative.mn_far_withdraw(p, width, height, cellSize, radius, a);
        }

        CoreNative.Check(withdrawn < 0 ? withdrawn : 0);
        CoreNative.CopyInto(bytes, allowed);
        return withdrawn;
    }
}
