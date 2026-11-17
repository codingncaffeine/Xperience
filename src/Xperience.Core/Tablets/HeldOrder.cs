using Xperience.Core.Desktop;
using Xperience.Core.Settings;

namespace Xperience.Core.Tablets;

/// <summary>
/// Which control is which once the tablet is turned. Assignments are kept in the order the controls run as the
/// tablet is held: the first key is the one at the top, or at the left when the keys lie along the top or the
/// bottom edge, and the first dial likewise. The tablet numbers its own keys from the top with the keys on the
/// left, so turned a quarter or a half its own last key is the first as held, and its two dials trade places.
/// Turned three quarters the keys lie along the bottom in the tablet's own order.
/// </summary>
public static class HeldOrder
{
    public static bool Reversed(TabletTurn turn) => turn is TabletTurn.Quarter or TabletTurn.Half;

    /// <summary>From a key's place as held to the tablet's own number for it, and just the same the other way; from 0.</summary>
    public static int Key(int index, TabletTurn turn) => Reversed(turn) ? Profile.KeyCount - 1 - index : index;

    /// <summary>From a dial's place as held to the tablet's own number for it, and just the same the other way; from 0.</summary>
    public static int Dial(int index, TabletTurn turn) => Reversed(turn) ? Profile.DialCount - 1 - index : index;
}
