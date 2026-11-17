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
    /// <param name="index">The key's place, from 0.</param>
    /// <param name="turn">How the tablet is turned.</param>
    /// <param name="keys">How many keys the tablet has; the active tablet's when not given.</param>
    public static int Key(int index, TabletTurn turn, int? keys = null) =>
        Reversed(turn) ? (keys ?? TabletModel.Active.Keys.Count) - 1 - index : index;

    /// <summary>From a dial's place as held to the tablet's own number for it, and just the same the other way; from 0.</summary>
    public static int Dial(int index, TabletTurn turn) => Reversed(turn) ? TabletModel.Active.Dials.Count - 1 - index : index;
}
