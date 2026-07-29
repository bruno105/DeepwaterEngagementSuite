using System.Collections.Generic;
using System.Linq;

namespace DeepwaterEngagementSuite.VoyagePlannerData;

public enum PieceType
{
    Cross,
    Straight,
    Corner,
    Tee,
    Single,
}

public record MapPiece(
    int Id,
    PieceType Type,
    Direction BaseConnections,
    List<Modifier> Modifiers)
{
    public readonly double GlobalModifier = Modifiers.Where(x => x.Scope == ModScope.Voyage).Sum(x => x.Weight);
    public readonly double LocalModifier = Modifiers.Where(x => x.Scope == ModScope.Adjacent).Sum(x => x.Weight);
    public readonly double OwnModifier = Modifiers.Where(x => x.Scope == ModScope.Self).Sum(x => x.Weight);
    public int DistinctRotations => Type switch
    {
        PieceType.Cross => 1,
        PieceType.Straight => 2,
        PieceType.Corner => 4,
        PieceType.Tee => 4,
        PieceType.Single => 4,
        _ => 4
    };

    public Direction GetConnections(int rotation)
    {
        return BaseConnections.RotateCcw(rotation);
    }
}
