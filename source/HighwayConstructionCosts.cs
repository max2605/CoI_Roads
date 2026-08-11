using System;
using Mafi.Base;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace GroundRoads;

internal readonly struct HighwayMaterialAmounts
{
    public readonly int Gravel;
    public readonly int Asphalt;

    public HighwayMaterialAmounts(int gravel, int asphalt)
    {
        Gravel = gravel;
        Asphalt = asphalt;
    }
}

/// <summary>
/// Balances the road as a compacted two-part gravel foundation plus one-part
/// asphalt surface. Asphalt itself is produced from 95% aggregate and 5%
/// heavy-oil binder. Costs are rounded per native planner entity because each
/// entity owns an independent construction buffer.
/// </summary>
internal static class HighwayConstructionCosts
{
    private const int GravelPerPavedLengthTile = 2;
    private const int AsphaltPerPavedLengthTile = 1;

    public static HighwayMaterialAmounts ForLength(double lengthTiles)
    {
        return ForScaledLength(lengthTiles, widthScale: 1.0);
    }

    public static HighwayMaterialAmounts ForScaledLength(
        double lengthTiles,
        double widthScale)
    {
        if (double.IsNaN(lengthTiles) ||
            double.IsInfinity(lengthTiles) ||
            lengthTiles <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTiles));
        }
        if (double.IsNaN(widthScale) ||
            double.IsInfinity(widthScale) ||
            widthScale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthScale));
        }

        var pavedLengthTiles = Math.Max(
            1,
            (int)Math.Ceiling(lengthTiles - 1e-8));
        return new HighwayMaterialAmounts(
            Math.Max(1, (int)Math.Ceiling(
                pavedLengthTiles * GravelPerPavedLengthTile * widthScale -
                1e-8)),
            Math.Max(1, (int)Math.Ceiling(
                pavedLengthTiles * AsphaltPerPavedLengthTile * widthScale -
                1e-8)));
    }

    public static HighwayMaterialAmounts ForNode(HighwayNodeKind kind)
    {
        // Equivalent paved centre-line lengths approximate each node's actual
        // four-tile-wide surface, including the roundabout's larger ring.
        return ForLength(kind switch
        {
            HighwayNodeKind.TIntersection => 12.0,
            HighwayNodeKind.CrossIntersection => 16.0,
            HighwayNodeKind.Roundabout => 24.0,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
    }

    public static EntityCosts CreateForLength(
        ProtoRegistrator registrator,
        double lengthTiles)
    {
        return Create(registrator, ForLength(lengthTiles));
    }

    public static EntityCosts CreateForScaledLength(
        ProtoRegistrator registrator,
        double lengthTiles,
        double widthScale)
    {
        return Create(
            registrator,
            ForScaledLength(lengthTiles, widthScale));
    }

    public static EntityCosts CreateForNode(
        ProtoRegistrator registrator,
        HighwayNodeKind kind)
    {
        return Create(registrator, ForNode(kind));
    }

    private static EntityCosts Create(
        ProtoRegistrator registrator,
        HighwayMaterialAmounts amounts)
    {
        EntityCostsTpl template = Costs.Build
            .Product(amounts.Gravel, Ids.Products.Gravel)
            .Product(amounts.Asphalt, GroundRoadMaterialIds.Asphalt);
        return template.MapToEntityCosts(registrator);
    }
}
