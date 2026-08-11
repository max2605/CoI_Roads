using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Roads;

namespace GroundRoads;

/// <summary>
/// Keeps separate traffic conflict areas apart. The validator participates in
/// both preview validation and authoritative command validation, so a stale
/// preview can never bypass the spacing rule.
/// </summary>
public sealed class HighwayJunctionPlacementValidator :
    IEntityAdditionValidator<LayoutEntityAddRequest>
{
    internal const int MinimumCenterDistanceTiles = 24;
    internal const int MinimumCenterDistanceSquared =
        MinimumCenterDistanceTiles * MinimumCenterDistanceTiles;

    internal static readonly Mafi.Localization.LocStrFormatted
        SpacingErrorForPlayer =
            GroundRoadTexts.Localized("error.junction-spacing");

    private readonly IEntitiesManager m_entitiesManager;

    public EntityValidatorPriority Priority => EntityValidatorPriority.High;

    public HighwayJunctionPlacementValidator(
        IEntitiesManager entitiesManager)
    {
        m_entitiesManager = entitiesManager;
    }

    public EntityValidationResult CanAdd(LayoutEntityAddRequest request)
    {
        if (request.Proto is not HighwayJunctionProto proposedProto)
        {
            return EntityValidationResult.Success;
        }

        var proposedCenter = GetCenter(
            proposedProto,
            request.Transform);
        foreach (var entity in
                 m_entitiesManager.GetAllEntitiesOfType<RoadEntityBase>())
        {
            if (entity.IsDestroyed ||
                (request.IgnoreForCollisions.HasValue &&
                 request.IgnoreForCollisions.Value(entity.Id)) ||
                entity.RoadProto is not HighwayJunctionProto existingProto)
            {
                continue;
            }

            var existingCenter = GetCenter(
                existingProto,
                entity.Transform);
            if (!AreCentersTooClose(proposedCenter, existingCenter))
            {
                continue;
            }

            if (request.RecordTileErrorsAndMetadata)
            {
                for (var index = 0;
                     index < request.OccupiedTiles.Length;
                     index++)
                {
                    request.SetTileError(index);
                }
            }

            return CreateSpacingError(proposedCenter, existingCenter);
        }

        return EntityValidationResult.Success;
    }

    internal static void FillExistingCenters(
        IEntitiesManager entitiesManager,
        Lyst<Tile2i> result)
    {
        result.Clear();
        foreach (var entity in
                 entitiesManager.GetAllEntitiesOfType<RoadEntityBase>())
        {
            if (!entity.IsDestroyed &&
                entity.RoadProto is HighwayJunctionProto proto)
            {
                result.Add(GetCenter(proto, entity.Transform));
            }
        }
    }

    internal static Tile2i GetCenter(
        HighwayJunctionProto proto,
        TileTransform transform)
    {
        return proto.Layout.TransformPoint_RelToCenterTile(
                RelTile3f.Zero,
                transform)
            .Tile3iRounded
            .Xy;
    }

    internal static bool AreCentersTooClose(
        Tile2i first,
        Tile2i second)
    {
        return first.DistanceSqrTo(second) <
            MinimumCenterDistanceSquared;
    }

    internal static EntityValidationResult CreateSpacingError(
        Tile2i proposedCenter,
        Tile2i existingCenter)
    {
        return EntityValidationResult.CreateError(
            SpacingErrorForPlayer,
            $"GroundRoads: junction center {proposedCenter} is less than " +
            $"{MinimumCenterDistanceTiles} tiles from existing junction " +
            $"center {existingCenter}.");
    }
}
