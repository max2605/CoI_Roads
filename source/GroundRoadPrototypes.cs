using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mafi;
using Mafi.Base;
using Mafi.Base.Prototypes.Trains;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Dynamic;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Mods;
using Mafi.Core.Ports.Io;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Terrain;
using Mafi.Core.Trains;
using Mafi.Curves;

namespace GroundRoads;

internal static class GroundRoadIds
{
    public static readonly Proto.ID ToolbarCategory =
        new("GroundRoads_ToolbarCategory");

    public static readonly Proto.ID RoadTileSurface =
        new("GroundRoads_RoadTileSurface");

    public static readonly StaticEntityProto.ID OneWayEntrance =
        new("GroundRoads_OneWayEntrance");

    public static readonly StaticEntityProto.ID OneWayExit =
        new("GroundRoads_OneWayExit");

    public static readonly StaticEntityProto.ID OneWayStraight =
        new("GroundRoads_OneWayStraight");

    public static readonly StaticEntityProto.ID OneWayCurve =
        new("GroundRoads_OneWayCurve");

    public static readonly StaticEntityProto.ID TwoWayEntrance =
        new("GroundRoads_TwoWayEntrance");

    public static readonly StaticEntityProto.ID TwoWayStraight =
        new("GroundRoads_TwoWayStraight");

    public static readonly StaticEntityProto.ID TwoWayCurve =
        new("GroundRoads_TwoWayCurve");

    public static readonly StaticEntityProto.ID TwoWayTileStraight =
        new("GroundRoads_TwoWayTileStraight");

    public static readonly StaticEntityProto.ID TwoWayTileAccess =
        new("GroundRoads_TwoWayTileAccess");

    public static readonly StaticEntityProto.ID TwoWayNativeStraightV2 =
        new("GroundRoads_TwoWayNativeStraightV2");

    public static readonly StaticEntityProto.ID HighwayOnRamp =
        new("GroundRoads_HighwayOnRampV2");

    public static readonly StaticEntityProto.ID HighwayOffRamp =
        new("GroundRoads_HighwayOffRampV2");

    public static readonly StaticEntityProto.ID HighwayOnRampV3 =
        new("GroundRoads_HighwayOnRampV3");

    public static readonly StaticEntityProto.ID HighwayOffRampV3 =
        new("GroundRoads_HighwayOffRampV3");

    public static readonly StaticEntityProto.ID HighwayTIntersection =
        new("GroundRoads_HighwayTIntersectionV1");

    public static readonly StaticEntityProto.ID HighwayCrossIntersection =
        new("GroundRoads_HighwayCrossIntersectionV1");

    public static readonly StaticEntityProto.ID HighwayRoundabout =
        new("GroundRoads_HighwayRoundaboutV1");
}

public class GroundRoadProto : RoadEntityProto, IRoadConnectionProto
{
    private readonly RelTile3f m_connectionPointAtStart;
    private readonly RelTile3f m_connectionPointAtEnd;

    public GroundRoadProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        RelTile3f connectionPointAtStart,
        RelTile3f connectionPointAtEnd,
        RoadEntityProtoBase.Gfx graphics)
        : base(
            id,
            strings,
            layout,
            costs,
            maxVehiclesSpeedPerTick,
            lanesSpecs,
            lanesData,
            lanesTrajectories,
            graphics,
            cannotBeBuiltByPlayer: false,
            cannotBeDestroyedByFlood: false,
            useTerrainHeightForVehicles: true)
    {
        m_connectionPointAtStart = connectionPointAtStart;
        m_connectionPointAtEnd = connectionPointAtEnd;
    }

    public Tile3i? GetTransformedNodeKeyAtStart(TileTransform transform)
    {
        return Layout.TransformPoint_RelToCenterTile(
                m_connectionPointAtStart,
                transform)
            .Tile3iRounded;
    }

    public Tile3i GetTransformedNodeKeyAtEnd(TileTransform transform)
    {
        return Layout.TransformPoint_RelToCenterTile(
                m_connectionPointAtEnd,
                transform)
            .Tile3iRounded;
    }

    public TileTransform GetTransformThatPositionsStartAt(
        Tile3i position,
        Rotation90 rotation,
        bool isReflected)
    {
        return GetTransformThatPositionsPointAt(
            m_connectionPointAtStart,
            position,
            rotation,
            isReflected);
    }

    private TileTransform GetTransformThatPositionsPointAt(
        RelTile3f point,
        Tile3i position,
        Rotation90 rotation,
        bool isReflected)
    {
        var orientationOnly =
            new TileTransform(Tile3i.Zero, rotation, isReflected);
        var pointOffset = Layout.TransformPoint_RelToCenterTile(
                point,
                orientationOnly)
            .Tile3iRounded;

        return new TileTransform(
            new Tile3i(
                position.X - pointOffset.X,
                position.Y - pointOffset.Y,
                position.Z - pointOffset.Z),
            rotation,
            isReflected);
    }
}

public class GroundRoadEntranceProto : RoadEntranceEntityProto,
    IRoadConnectionProto
{
    private readonly RelTile3f m_terrainPoint;
    private readonly RelTile3f m_connectionPoint;

    /// <summary>
    /// V3 highway ramps own an explicit world-space traffic heading. Legacy
    /// entrances leave this unset and remain registered only for save
    /// compatibility.
    /// </summary>
    public TrainTrackNodeDirection? FixedHighwayDirection { get; }

    public GroundRoadEntranceProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        ImmutableArray<LaneTerrainConnectionSpec> terrainConnections,
        RelTile3f terrainPoint,
        RelTile3f connectionPoint,
        RoadEntityProtoBase.Gfx graphics,
        TrainTrackNodeDirection? fixedHighwayDirection = null)
        : base(
            id,
            strings,
            layout,
            costs,
            maxVehiclesSpeedPerTick,
            lanesSpecs,
            lanesData,
            lanesTrajectories,
            terrainConnections,
            graphics,
            useTerrainHeightForVehicles: true,
            cannotBeBuiltByPlayer: false,
            cannotBeDestroyedByFlood: false)
    {
        m_terrainPoint = terrainPoint;
        m_connectionPoint = connectionPoint;
        FixedHighwayDirection = fixedHighwayDirection;
    }

    public Tile3i? GetTransformedNodeKeyAtStart(TileTransform transform)
    {
        return null;
    }

    public Tile3i GetTransformedNodeKeyAtEnd(TileTransform transform)
    {
        return Layout.TransformPoint_RelToCenterTile(
                m_connectionPoint,
                transform)
            .Tile3iRounded;
    }

    public Tile3i GetTransformedTerrainPoint(TileTransform transform)
    {
        return Layout.TransformPoint_RelToCenterTile(
                m_terrainPoint,
                transform)
            .Tile3iRounded;
    }

    public TileTransform GetTransformThatPositionsTerrainAt(
        Tile3i position,
        Rotation90 rotation,
        bool isReflected)
    {
        return GetTransformThatPositionsPointAt(
            m_terrainPoint,
            position,
            rotation,
            isReflected);
    }

    public TileTransform GetTransformThatPositionsConnectionAt(
        Tile3i position,
        Rotation90 rotation,
        bool isReflected)
    {
        return GetTransformThatPositionsPointAt(
            m_connectionPoint,
            position,
            rotation,
            isReflected);
    }

    private TileTransform GetTransformThatPositionsPointAt(
        RelTile3f point,
        Tile3i position,
        Rotation90 rotation,
        bool isReflected)
    {
        var orientationOnly =
            new TileTransform(Tile3i.Zero, rotation, isReflected);
        var pointOffset = Layout.TransformPoint_RelToCenterTile(
                point,
                orientationOnly)
            .Tile3iRounded;

        return new TileTransform(
            new Tile3i(
                position.X - pointOffset.X,
                position.Y - pointOffset.Y,
                position.Z - pointOffset.Z),
            rotation,
            isReflected);
    }
}

/// <summary>
/// A native road piece whose geometry is derived from one of the public train
/// track planning prototypes. The train path finder plans the curve; this
/// prototype is the actual, saveable road entity created in the world.
/// </summary>
public sealed class HighwaySegmentProto : GroundRoadProto,
    IHighwayMainlineProto,
    IHighwayPortProto,
    IHighwayLaneProfileProto
{
    public TrainTrackProto SourceTrackProto { get; }

    /// <summary>
    /// Reflections reverse handedness. A train has only a centre line, but a
    /// two-way road has a left and a right lane. Reflected train pieces must
    /// therefore use lane geometry whose handedness was swapped beforehand.
    /// </summary>
    public bool CorrectsReflectedHandedness { get; }

    public HighwayRoadProfile RoadProfile { get; }

    public HighwayTier Tier => RoadProfile.Tier;

    public int LanesPerDirection => RoadProfile.LanesPerDirection;

    public double VisualLaneWidthTiles => RoadProfile.VisualLaneWidthTiles;

    public double RoadHalfWidthTiles => RoadProfile.RoadHalfWidthTiles;

    public bool ParticipatesInHighwayNetwork => true;

    public HighwaySegmentProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        RelTile3f connectionPointAtStart,
        RelTile3f connectionPointAtEnd,
        RoadEntityProtoBase.Gfx graphics,
        TrainTrackProto sourceTrackProto,
        bool correctsReflectedHandedness,
        HighwayRoadProfile roadProfile)
        : base(
            id,
            strings,
            layout,
            costs,
            maxVehiclesSpeedPerTick,
            lanesSpecs,
            lanesData,
            lanesTrajectories,
            connectionPointAtStart,
            connectionPointAtEnd,
            graphics)
    {
        SourceTrackProto = sourceTrackProto;
        CorrectsReflectedHandedness = correctsReflectedHandedness;
        RoadProfile = roadProfile;
    }

    public bool IsPassingLane(int laneIndex) =>
        RoadProfile.IsPassingLane(laneIndex);

    public TileTransform MapTrackTransform(TileTransform trackTransform)
    {
        // Use the exact graph node used by the train planner. Going through
        // the source layout is only visually equivalent; its origin/rounding
        // rules are not the graph's placement contract.
        SourceTrackProto.GetTransformedGraphNodes(
            trackTransform,
            startBridge: false,
            endBridge: false,
            out var trackStartNode,
            out _);
        var trackStart = trackStartNode.Position.Tile3iRounded;

        var orientation = new TileTransform(
            Tile3i.Zero,
            trackTransform.Rotation,
            trackTransform.IsReflected);
        // The track plan is centered between the two highway lanes. Aligning
        // to lane 0 would shift every individual piece sideways and leave a
        // one-tile discontinuity at curves. Lane 1 runs in reverse, so its
        // end is the matching point at the segment start.
        var roadCenterAtStart = LanesData[0].StartPosition.Average(
            LanesData[1].EndPosition);
        var roadStart = Layout.TransformPoint_RelToCenterTile(
                roadCenterAtStart,
                orientation)
            .Tile3iRounded;

        return new TileTransform(
            new Tile3i(
                trackStart.X - roadStart.X,
                trackStart.Y - roadStart.Y,
                trackStart.Z - roadStart.Z),
            trackTransform.Rotation,
            trackTransform.IsReflected);
    }

    // One physical port per end. T4's parallel lanes are a lane bundle behind
    // that port; exposing them as separate clickable ports would make the
    // planner snap ambiguously to the same road centre.
    public int HighwayPortCount => 2;

    public HighwayPort GetHighwayPort(int index, TileTransform transform)
    {
        if (index < 0 || index >= HighwayPortCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var isAtEnd = index == 1;
        const int forwardLaneIndex = 0;
        const int reverseLaneIndex = 1;
        var center = Layout.TransformPoint_RelToCenterTile(
                isAtEnd
                    ? LanesData[forwardLaneIndex].EndPosition.Average(
                        LanesData[reverseLaneIndex].StartPosition)
                    : LanesData[forwardLaneIndex].StartPosition.Average(
                        LanesData[reverseLaneIndex].EndPosition),
                transform)
            .Tile3iRounded;
        return isAtEnd
            ? new HighwayPort(
                center,
                GetTransformedStartGraphNode(reverseLaneIndex, transform),
                GetTransformedEndGraphNode(forwardLaneIndex, transform),
                attachmentCollisionSeamRange:
                    (int)Math.Ceiling(RoadHalfWidthTiles),
                tier: Tier)
            : new HighwayPort(
                center,
                GetTransformedStartGraphNode(forwardLaneIndex, transform),
                GetTransformedEndGraphNode(reverseLaneIndex, transform),
                attachmentCollisionSeamRange:
                    (int)Math.Ceiling(RoadHalfWidthTiles),
                tier: Tier);
    }
}

public sealed class HighwayOnRampProto : GroundRoadEntranceProto,
    IHighwayNetworkProto
{
    // Compatibility-only prototype: hidden from the toolbar and deliberately
    // excluded from all new highway routes.
    public bool ParticipatesInHighwayNetwork => false;

    public HighwayOnRampProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        ImmutableArray<LaneTerrainConnectionSpec> terrainConnections,
        RelTile3f terrainPoint,
        RelTile3f connectionPoint,
        RoadEntityProtoBase.Gfx graphics,
        TrainTrackNodeDirection? fixedHighwayDirection = null)
        : base(id, strings, layout, costs, maxVehiclesSpeedPerTick,
            lanesSpecs, lanesData, lanesTrajectories, terrainConnections,
            terrainPoint, connectionPoint, graphics,
            fixedHighwayDirection)
    {
    }
}

public sealed class HighwayOffRampProto : GroundRoadEntranceProto,
    IHighwayNetworkProto
{
    // Compatibility-only prototype: hidden from the toolbar and deliberately
    // excluded from all new highway routes.
    public bool ParticipatesInHighwayNetwork => false;

    public HighwayOffRampProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        ImmutableArray<LaneTerrainConnectionSpec> terrainConnections,
        RelTile3f terrainPoint,
        RelTile3f connectionPoint,
        RoadEntityProtoBase.Gfx graphics,
        TrainTrackNodeDirection? fixedHighwayDirection = null)
        : base(id, strings, layout, costs, maxVehiclesSpeedPerTick,
            lanesSpecs, lanesData, lanesTrajectories, terrainConnections,
            terrainPoint, connectionPoint, graphics,
            fixedHighwayDirection)
    {
    }
}

/// <summary>
/// A short, native road entity that has normal occupied terrain geometry and
/// its own terrain entrances. Keeping the line endpoints separate from the
/// entrance endpoints lets the drag tool connect several pieces precisely.
/// </summary>
public sealed class GroundRoadAccessibleSegmentProto : RoadEntranceEntityProto
{
    private readonly RelTile3f m_segmentStartPoint;
    private readonly RelTile3f m_segmentEndPoint;

    public GroundRoadAccessibleSegmentProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        ImmutableArray<LaneTerrainConnectionSpec> terrainConnections,
        RelTile3f segmentStartPoint,
        RelTile3f segmentEndPoint,
        RoadEntityProtoBase.Gfx graphics)
        : base(
            id,
            strings,
            layout,
            costs,
            maxVehiclesSpeedPerTick,
            lanesSpecs,
            lanesData,
            lanesTrajectories,
            terrainConnections,
            graphics,
            useTerrainHeightForVehicles: true,
            cannotBeBuiltByPlayer: false,
            cannotBeDestroyedByFlood: false)
    {
        m_segmentStartPoint = segmentStartPoint;
        m_segmentEndPoint = segmentEndPoint;
    }

    public Tile3i GetTransformedSegmentStart(TileTransform transform)
    {
        return Layout.TransformPoint_RelToCenterTile(
                m_segmentStartPoint,
                transform)
            .Tile3iRounded;
    }

    public Tile3i GetTransformedSegmentEnd(TileTransform transform)
    {
        return Layout.TransformPoint_RelToCenterTile(
                m_segmentEndPoint,
                transform)
            .Tile3iRounded;
    }

    public TileTransform GetTransformThatPositionsSegmentStartAt(
        Tile3i position,
        Rotation90 rotation,
        bool isReflected)
    {
        var orientationOnly =
            new TileTransform(Tile3i.Zero, rotation, isReflected);
        var pointOffset = Layout.TransformPoint_RelToCenterTile(
                m_segmentStartPoint,
                orientationOnly)
            .Tile3iRounded;

        return new TileTransform(
            new Tile3i(
                position.X - pointOffset.X,
                position.Y - pointOffset.Y,
                position.Z - pointOffset.Z),
            rotation,
            isReflected);
    }
}

public sealed class GroundRoadGfx : RoadEntityProtoBase.Gfx
{
    private static readonly PropertyInfo IconPathProperty =
        typeof(LayoutEntityProto.Gfx).GetProperty(
            nameof(LayoutEntityProto.Gfx.IconPath),
            BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(
            nameof(LayoutEntityProto.Gfx),
            nameof(LayoutEntityProto.Gfx.IconPath));

    private readonly string m_iconPath;

    public GroundRoadGfx(
        ImmutableArray<ToolbarEntryData> categories,
        string iconPath)
        : base(
            "EMPTY",
            categories,
            RelTile3f.Zero,
            45.Degrees(),
            useInstancedRendering: false)
    {
        m_iconPath = iconPath;
    }

    public override void Initialize(ILayoutEntityProto proto)
    {
        base.Initialize(proto);
        IconPathProperty.SetValue(this, m_iconPath);
    }
}

internal static class GroundRoadsData
{
    private const string RoadIcon =
        "Assets/Unity/Generated/Icons/Vehicle/TruckT2.png";

    private const RoadLaneType BasicLane =
        RoadLaneType.MaskAllowAll | RoadLaneType.BasicLaneFlag;

    private const RoadLaneType TerrainLane =
        BasicLane | RoadLaneType.TerrainConnectionFlag;

    private static readonly CubicBezierCurve2f FlatHeight =
        Curve((0.0, 0.0), (0.25, 0.0), (0.75, 0.0), (1.0, 0.0));

    private static RelTile1f s_maxRoadVehicleSpeedPerTick = 1.4.Tiles();

    public static void Register(ProtoRegistrator registrator)
    {
        s_maxRoadVehicleSpeedPerTick =
            ComputeMaxRoadVehicleSpeedPerTick(registrator.PrototypesDb);
        var vehiclesCategory = registrator.PrototypesDb
            .GetOrThrow<ToolbarCategoryProto>(Ids.ToolbarCategories.Vehicles);
        var roadsCategory = registrator.PrototypesDb.Add(
            new ToolbarCategoryProto(
                GroundRoadIds.ToolbarCategory,
                Proto.CreateStr(
                    GroundRoadIds.ToolbarCategory,
                    GroundRoadTexts.Get("category.highways.name"),
                    GroundRoadTexts.Get("category.highways.description")),
                order: 100f,
                iconPath: RoadIcon,
                parentCategory: vehiclesCategory));
        // All prototypes from versions 1.x remain registered for loading old
        // saves, but they are deliberately absent from the build menu.
        var categories = ImmutableArray<ToolbarEntryData>.Empty;
        var highwayCategories = ImmutableArray.Create(
            new ToolbarEntryData(roadsCategory, order: 0));

        // Kept hidden solely so saves that contain the 1.3.1 surface still
        // resolve its prototype. It no longer participates in the road tool.
        RegisterLegacyRoadTileSurface(registrator);

        RegisterEntrance(
            registrator,
            GroundRoadIds.OneWayEntrance,
            GroundRoadTexts.Get("legacy.one-way-entrance.name"),
            GroundRoadTexts.Get("legacy.one-way-entrance.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, 0.0),
                        (1.5, 0.0),
                        (4.5, 0.0),
                        (6.0, 0.0)),
                    TerrainLane,
                    BasicLane)),
            ImmutableArray.Create((0, true)),
            categories);

        RegisterEntrance(
            registrator,
            GroundRoadIds.OneWayExit,
            GroundRoadTexts.Get("legacy.one-way-exit.name"),
            GroundRoadTexts.Get("legacy.one-way-exit.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, 0.0),
                        (1.5, 0.0),
                        (4.5, 0.0),
                        (6.0, 0.0)),
                    BasicLane,
                    TerrainLane)),
            ImmutableArray.Create((0, false)),
            categories);

        RegisterRoad(
            registrator,
            GroundRoadIds.OneWayStraight,
            GroundRoadTexts.Get("legacy.one-way-straight.name"),
            GroundRoadTexts.Get("legacy.one-way-straight.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, 0.0),
                        (3.0, 0.0),
                        (9.0, 0.0),
                        (12.0, 0.0)),
                    BasicLane,
                    BasicLane)),
            categories);

        RegisterRoad(
            registrator,
            GroundRoadIds.OneWayCurve,
            GroundRoadTexts.Get("legacy.one-way-curve.name"),
            GroundRoadTexts.Get("legacy.one-way-curve.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, 0.0),
                        (6.627, 0.0),
                        (12.0, 5.373),
                        (12.0, 12.0)),
                    BasicLane,
                    BasicLane)),
            categories,
            segmentsPer10Tiles: 8);

        RegisterEntrance(
            registrator,
            GroundRoadIds.TwoWayEntrance,
            GroundRoadTexts.Get("legacy.two-way-entrance.name"),
            GroundRoadTexts.Get("legacy.two-way-entrance.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, -2.0),
                        (1.5, -2.0),
                        (4.5, -2.0),
                        (6.0, -2.0)),
                    TerrainLane,
                    BasicLane),
                Lane(
                    Curve(
                        (6.0, 2.0),
                        (4.5, 2.0),
                        (1.5, 2.0),
                        (0.0, 2.0)),
                    BasicLane,
                    TerrainLane)),
            ImmutableArray.Create((0, true), (1, false)),
            categories);

        RegisterRoad(
            registrator,
            GroundRoadIds.TwoWayStraight,
            GroundRoadTexts.Get("legacy.two-way-straight.name"),
            GroundRoadTexts.Get("legacy.two-way-straight.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, -2.0),
                        (3.0, -2.0),
                        (9.0, -2.0),
                        (12.0, -2.0)),
                    BasicLane,
                    BasicLane),
                Lane(
                    Curve(
                        (12.0, 2.0),
                        (9.0, 2.0),
                        (3.0, 2.0),
                        (0.0, 2.0)),
                    BasicLane,
                    BasicLane)),
            categories);

        RegisterRoad(
            registrator,
            GroundRoadIds.TwoWayCurve,
            GroundRoadTexts.Get("legacy.two-way-curve.name"),
            GroundRoadTexts.Get("legacy.two-way-curve.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, -2.0),
                        (7.732, -2.0),
                        (14.0, 4.268),
                        (14.0, 12.0)),
                    BasicLane,
                    BasicLane),
                Lane(
                    Curve(
                        (10.0, 12.0),
                        (10.0, 6.477),
                        (5.523, 2.0),
                        (0.0, 2.0)),
                    BasicLane,
                    BasicLane)),
            categories,
            segmentsPer10Tiles: 8);

        // The tile road is deliberately built from an empty layout. It keeps
        // the native road graph (and therefore the game's 50% road routing
        // cost), but it does not reserve or block terrain cells. Trucks may
        // approach, cross, or leave it like a terrain surface.
        RegisterRoad(
            registrator,
            GroundRoadIds.TwoWayTileStraight,
            GroundRoadTexts.Get("legacy.tile-straight.name"),
            GroundRoadTexts.Get("legacy.tile-straight.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, -2.0),
                        (1.0, -2.0),
                        (3.0, -2.0),
                        (4.0, -2.0)),
                    BasicLane,
                    BasicLane),
                Lane(
                    Curve(
                        (4.0, 2.0),
                        (3.0, 2.0),
                        (1.0, 2.0),
                        (0.0, 2.0)),
                    BasicLane,
                    BasicLane)),
            ImmutableArray<ToolbarEntryData>.Empty,
            makeLayoutNonBlocking: true);

        // This one-tile spur joins the terrain graph to a road-tile joint.
        // It is non-blocking and overlaps the visible road, so the user sees
        // one continuous surface while pathfinding gets frequent entrances.
        RegisterEntrance(
            registrator,
            GroundRoadIds.TwoWayTileAccess,
            GroundRoadTexts.Get("legacy.tile-access.name"),
            GroundRoadTexts.Get("legacy.tile-access.description"),
            ImmutableArray.Create(
                Lane(
                    Curve(
                        (0.0, -2.0),
                        (0.25, -2.0),
                        (0.75, -2.0),
                        (1.0, -2.0)),
                    TerrainLane,
                    BasicLane),
                Lane(
                    Curve(
                        (1.0, 2.0),
                        (0.75, 2.0),
                        (0.25, 2.0),
                        (0.0, 2.0)),
                    BasicLane,
                    TerrainLane)),
            ImmutableArray.Create((0, true), (1, false)),
            ImmutableArray<ToolbarEntryData>.Empty,
            makeLayoutNonBlocking: true);

        RegisterAccessibleSegment(
            registrator,
            GroundRoadIds.TwoWayNativeStraightV2,
            GroundRoadTexts.Get("legacy.native-straight.name"),
            GroundRoadTexts.Get("legacy.native-straight.description"),
            ImmutableArray.Create(
                // Main lanes are split in the middle. The internal graph node
                // is where the four short terrain connectors join.
                Lane(
                    Curve(
                        (0.0, -2.0),
                        (0.5, -2.0),
                        (1.5, -2.0),
                        (2.0, -2.0)),
                    BasicLane,
                    BasicLane),
                Lane(
                    Curve(
                        (2.0, -2.0),
                        (2.5, -2.0),
                        (3.5, -2.0),
                        (4.0, -2.0)),
                    BasicLane,
                    BasicLane),
                Lane(
                    Curve(
                        (4.0, 2.0),
                        (3.5, 2.0),
                        (2.5, 2.0),
                        (2.0, 2.0)),
                    BasicLane,
                    BasicLane),
                Lane(
                    Curve(
                        (2.0, 2.0),
                        (1.5, 2.0),
                        (0.5, 2.0),
                        (0.0, 2.0)),
                    BasicLane,
                    BasicLane),
                // Lower side: enter and leave the forward lane.
                Lane(
                    Curve(
                        (1.0, -4.0),
                        (1.0, -3.0),
                        (1.0, -2.0),
                        (2.0, -2.0)),
                    TerrainLane,
                    BasicLane),
                Lane(
                    Curve(
                        (2.0, -2.0),
                        (3.0, -2.0),
                        (3.0, -3.0),
                        (3.0, -4.0)),
                    BasicLane,
                    TerrainLane),
                // Upper side: enter and leave the reverse lane.
                Lane(
                    Curve(
                        (3.0, 4.0),
                        (3.0, 3.0),
                        (3.0, 2.0),
                        (2.0, 2.0)),
                    TerrainLane,
                    BasicLane),
                Lane(
                    Curve(
                        (2.0, 2.0),
                        (1.0, 2.0),
                        (1.0, 3.0),
                        (1.0, 4.0)),
                    BasicLane,
                    TerrainLane)),
            ImmutableArray.Create(
                (4, true),
                (5, false),
                (6, true),
                (7, false)));

        RegisterHighwayRamp(
            registrator,
            GroundRoadIds.HighwayOnRamp,
            GroundRoadTexts.Get("prototype.on-ramp.name"),
            GroundRoadTexts.Get("prototype.on-ramp.description"),
            isOnRamp: true,
            ImmutableArray<ToolbarEntryData>.Empty);

        RegisterHighwayRamp(
            registrator,
            GroundRoadIds.HighwayOffRamp,
            GroundRoadTexts.Get("prototype.off-ramp.name"),
            GroundRoadTexts.Get("prototype.off-ramp.description"),
            isOnRamp: false,
            ImmutableArray<ToolbarEntryData>.Empty);

        // V2 and V3 stay registered under their original IDs so existing
        // saves can still be opened. They remain hidden and excluded from the
        // active highway graph.
        RegisterHighwayRamp(
            registrator,
            GroundRoadIds.HighwayOnRampV3,
            GroundRoadTexts.Get("prototype.on-ramp.name"),
            GroundRoadTexts.Get("prototype.on-ramp-v3.description"),
            isOnRamp: true,
            ImmutableArray<ToolbarEntryData>.Empty,
            hasFixedWorldDirection: true);

        RegisterHighwayRamp(
            registrator,
            GroundRoadIds.HighwayOffRampV3,
            GroundRoadTexts.Get("prototype.off-ramp.name"),
            GroundRoadTexts.Get("prototype.off-ramp-v3.description"),
            isOnRamp: false,
            ImmutableArray<ToolbarEntryData>.Empty,
            hasFixedWorldDirection: true);

        RegisterHighwaySegmentsFromTrainPlanner(registrator);
        HighwayNetworkData.Register(
            registrator,
            roadsCategory,
            s_maxRoadVehicleSpeedPerTick);

        Log.Info(
            "GroundRoads: registered the train-planned highway library " +
            "including terrain elevation pieces and automatic " +
            "traffic-director access. T/+ intersections and the roundabout " +
            "are available. Historical on/off-ramp IDs remain hidden for " +
            "save compatibility. Road speed cap is " +
            $"{s_maxRoadVehicleSpeedPerTick} per tick.");
    }

    private static RelTile1f ComputeMaxRoadVehicleSpeedPerTick(
        ProtosDb protosDb)
    {
        // A finite limit is mandatory. DrivingEntity uses the larger of the
        // vehicle and road maxima for lane look-ahead; MaxValue consumes all
        // curve samples and makes a vehicle leave road mode immediately.
        var result = 1.4.Tiles();
        foreach (var vehicleProto in protosDb.All<DrivingEntityProto>())
        {
            if (vehicleProto.PathFindingParams.RoadLaneTypeMask ==
                RoadLaneType.MaskAllowNone)
            {
                continue;
            }

            result = result.Max(
                vehicleProto.DrivingData.MaxForwardsSpeed.ScaledBy(
                    140.Percent()));
        }

        return result;
    }

    private static void RegisterHighwaySegmentsFromTrainPlanner(
        ProtoRegistrator registrator)
    {
        var registered = 0;
        foreach (var track in registrator.PrototypesDb.All<TrainTrackProto>()
                     .Where(x => !x.IsObsolete &&
                                  !x.IgnoreInPathFinder &&
                                  x.IsElevated))
        {
            foreach (var profile in HighwayRoadProfile.All)
            {
                var id = new StaticEntityProto.ID(
                    "GroundRoads_HighwayTrack_" + profile.IdToken +
                    SanitizeId(track.Id.Value));
                var normalLanes = CreateHighwayLaneTrajectories(
                    track.TrajectoryData.Segments,
                    profile,
                    correctsReflectedHandedness: false);
                var reflectedLanes = CreateHighwayLaneTrajectories(
                    track.TrajectoryData.Segments,
                    profile,
                    correctsReflectedHandedness: true);

                RegisterHighwaySegmentVariant(
                    registrator,
                    id,
                    track,
                    normalLanes,
                    correctsReflectedHandedness: false,
                    profile);

                // Keep the historical suffix for Standard so existing saves
                // resolve the exact same reflected prototype ID.
                var mirroredSuffix =
                    profile.Tier == HighwayTier.Standard
                        ? "_MirroredV3"
                        : "_MirroredV1";
                RegisterHighwaySegmentVariant(
                    registrator,
                    new StaticEntityProto.ID(id.Value + mirroredSuffix),
                    track,
                    reflectedLanes,
                    correctsReflectedHandedness: true,
                    profile);
                registered += 2;
            }
        }

        Log.Info(
            $"GroundRoads: {registered} train-planned highway geometries " +
            "registered.");
    }

    private static void RegisterHighwaySegmentVariant(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        TrainTrackProto track,
        ImmutableArray<RoadLaneTrajectory> laneTrajectories,
        bool correctsReflectedHandedness,
        HighwayRoadProfile profile)
    {
        var trajectory = track.TrajectoryData;
        var laneSpecs = new ImmutableArrayBuilder<RoadLaneSpec>(
            laneTrajectories.Length);
        var laneDataBuilder = new ImmutableArrayBuilder<RoadLaneMetadata>(
            laneTrajectories.Length);
        for (var pairIndex = 0;
             pairIndex < profile.LaneOffsetsTiles.Length;
             pairIndex++)
        {
            var signedOffset = correctsReflectedHandedness
                ? -profile.LaneOffsetsTiles[pairIndex]
                : profile.LaneOffsetsTiles[pairIndex];
            var forward = laneTrajectories[pairIndex * 2];
            var reverse = laneTrajectories[pairIndex * 2 + 1];
            laneSpecs[pairIndex * 2] = new RoadLaneSpec(
                trajectory.TrajectoryCurve,
                signedOffset.Tiles(),
                trajectory.HeightCurve,
                BasicLane,
                BasicLane);
            laneSpecs[pairIndex * 2 + 1] = new RoadLaneSpec(
                trajectory.TrajectoryCurve.ReverseControlPoints(),
                signedOffset.Tiles(),
                ReverseHeightCurve(trajectory.HeightCurve),
                BasicLane,
                BasicLane);
            laneDataBuilder[pairIndex * 2] = new RoadLaneMetadata(
                forward.LaneCenterSamples.First,
                forward.LaneCenterSamples.Last,
                trajectory.Segments.StartDirection,
                trajectory.Segments.EndDirection,
                BasicLane,
                BasicLane,
                forward.SegmentLengthsPrefixSums.Last);
            laneDataBuilder[pairIndex * 2 + 1] = new RoadLaneMetadata(
                reverse.LaneCenterSamples.First,
                reverse.LaneCenterSamples.Last,
                trajectory.Segments.EndDirection.Inversed(),
                trajectory.Segments.StartDirection.Inversed(),
                BasicLane,
                BasicLane,
                reverse.SegmentLengthsPrefixSums.Last);
        }
        var lanes = laneSpecs.GetImmutableArrayAndClear();
        var laneData = laneDataBuilder.GetImmutableArrayAndClear();
        var constructionCosts =
            ShouldUseConstructionMaterialsForHighwaySegment(
                track.HasElevationChange)
                ? HighwayConstructionCosts.CreateForScaledLength(
                    registrator,
                    laneTrajectories[0].SegmentLengthsPrefixSums.Last.Value
                        .ToDouble(),
                    profile.ConstructionWidthScale)
                : EntityCosts.None;

        registrator.PrototypesDb.Add(
            new HighwaySegmentProto(
                id,
                Proto.CreateStr(
                    id,
                    GroundRoadTexts.Get(
                        profile.Tier == HighwayTier.Standard
                            ? "prototype.highway-segment.name"
                            : profile.Tier == HighwayTier.HeavyT3
                                ? "prototype.highway-segment-t3.name"
                                : "prototype.highway-segment-t4.name"),
                    GroundRoadTexts.Get(
                        profile.Tier == HighwayTier.Standard
                            ? "prototype.highway-segment.description"
                            : profile.Tier == HighwayTier.HeavyT3
                                ? "prototype.highway-segment-t3.description"
                                : "prototype.highway-segment-t4.description")),
                CreateMinimalHighwayLayout(track.Layout),
                constructionCosts,
                track.MaxSpeedTilesPerTick.Min(
                    s_maxRoadVehicleSpeedPerTick),
                lanes,
                laneData,
                laneTrajectories,
                ComputeRoadCenterPoint(laneData, atStart: true),
                ComputeRoadCenterPoint(laneData, atStart: false),
                new GroundRoadGfx(
                    ImmutableArray<ToolbarEntryData>.Empty,
                    RoadIcon),
                track,
                correctsReflectedHandedness,
                profile));
    }

    private static bool ShouldUseConstructionMaterialsForHighwaySegment(
        bool hasElevationChange)
    {
        // A construction site in the middle of an unfinished ramp cannot be
        // reached by trucks: the road needed to climb there is precisely the
        // entity still waiting for delivery. Complete only those G4/G8 pieces
        // immediately so they open access to the paid horizontal continuation.
        return !hasElevationChange;
    }

    private static RoadLaneTrajectory ReverseLaneTrajectory(
        RoadLaneTrajectory source)
    {
        var count = source.LaneCenterSamples.Length;
        var positions = new ImmutableArrayBuilder<RelTile3f>(count);
        var directions = new ImmutableArrayBuilder<RelTile3f>(count);
        for (var index = 0; index < count; index++)
        {
            var sourceIndex = count - index - 1;
            positions[index] = source.LaneCenterSamples[sourceIndex];
            directions[index] = -source.LaneDirectionSamples[sourceIndex];
        }

        var reversedPositions = positions.GetImmutableArrayAndClear();
        return new RoadLaneTrajectory(
            reversedPositions,
            directions.GetImmutableArrayAndClear(),
            ComputeLengthPrefixes(reversedPositions));
    }

    private static CubicBezierCurve2f ReverseHeightCurve(
        CubicBezierCurve2f source)
    {
        // Height curves use X as distance along the directed XY trajectory.
        // Reversing only the control-point order would make X decrease and is
        // therefore not a valid height function. Mirror X around the curve's
        // extent while reversing the height samples instead.
        var points = source.ControlPoints;
        var mirroredX = points.First.X + points.Last.X;
        return new CubicBezierCurve2f(
            ImmutableArray.Create(
                new Vector2f(
                    mirroredX - points[3].X,
                    points[3].Y),
                new Vector2f(
                    mirroredX - points[2].X,
                    points[2].Y),
                new Vector2f(
                    mirroredX - points[1].X,
                    points[1].Y),
                new Vector2f(
                    mirroredX - points[0].X,
                    points[0].Y)));
    }

    private static void CreateHighwayLaneTrajectories(
        TrainTrackSegmentsRel source,
        out RoadLaneTrajectory forward,
        out RoadLaneTrajectory reverse)
    {
        CreateHighwayLaneTrajectoryPair(
            source,
            Fix32.One,
            out forward,
            out reverse);
    }

    private static ImmutableArray<RoadLaneTrajectory>
        CreateHighwayLaneTrajectories(
            TrainTrackSegmentsRel source,
            HighwayRoadProfile profile,
            bool correctsReflectedHandedness)
    {
        var result = new ImmutableArrayBuilder<RoadLaneTrajectory>(
            profile.LaneOffsetsTiles.Length * 2);
        for (var pairIndex = 0;
             pairIndex < profile.LaneOffsetsTiles.Length;
             pairIndex++)
        {
            CreateHighwayLaneTrajectoryPair(
                source,
                profile.LaneOffsetsTiles[pairIndex],
                out var forward,
                out var reverse);
            result[pairIndex * 2] = correctsReflectedHandedness
                ? ReverseLaneTrajectory(reverse)
                : forward;
            result[pairIndex * 2 + 1] = correctsReflectedHandedness
                ? ReverseLaneTrajectory(forward)
                : reverse;
        }

        return result.GetImmutableArrayAndClear();
    }

    private static void CreateHighwayLaneTrajectoryPair(
        TrainTrackSegmentsRel source,
        Fix32 laneOffsetTiles,
        out RoadLaneTrajectory forward,
        out RoadLaneTrajectory reverse)
    {
        var count = source.Positions.Length;
        var forwardPositions =
            new ImmutableArrayBuilder<RelTile3f>(count);
        var forwardDirections =
            new ImmutableArrayBuilder<RelTile3f>(count);
        var reversePositions =
            new ImmutableArrayBuilder<RelTile3f>(count);
        var reverseDirections =
            new ImmutableArrayBuilder<RelTile3f>(count);

        for (var index = 0; index < count; index++)
        {
            // Road graph keys use TrainTrackNodeDirection, not the raw Bezier
            // tangent. At a seam the two raw normalized tangents can differ by
            // a fixed-point unit even though both quantize to the same 22.5°
            // track direction. Derive the endpoint offsets from that exact
            // graph direction so adjacent pieces produce byte-identical node
            // positions as well as identical direction keys.
            var direction = index == 0
                ? GetExactDirection(source.StartDirection)
                : index == count - 1
                    ? GetExactDirection(source.EndDirection)
                    : source.DirectionsNormalized[index];
            var lateral =
                laneOffsetTiles *
                direction.Xy.Normalized.RightOrthogonalVector;
            forwardPositions[index] =
                source.Positions[index] + new RelTile3f(lateral, Fix32.Zero);
            forwardDirections[index] = direction;

            var sourceIndex = count - index - 1;
            var sourceDirection = sourceIndex == 0
                ? GetExactDirection(source.StartDirection)
                : sourceIndex == count - 1
                    ? GetExactDirection(source.EndDirection)
                    : source.DirectionsNormalized[sourceIndex];
            var reverseDirection = -sourceDirection;
            var reverseLateral =
                laneOffsetTiles *
                reverseDirection.Xy.Normalized.RightOrthogonalVector;
            reversePositions[index] =
                source.Positions[sourceIndex] +
                new RelTile3f(reverseLateral, Fix32.Zero);
            reverseDirections[index] = reverseDirection;
        }

        var forwardPos = forwardPositions.GetImmutableArrayAndClear();
        var forwardDir = forwardDirections.GetImmutableArrayAndClear();
        var reversePos = reversePositions.GetImmutableArrayAndClear();
        var reverseDir = reverseDirections.GetImmutableArrayAndClear();
        forward = new RoadLaneTrajectory(
            forwardPos,
            forwardDir,
            ComputeLengthPrefixes(forwardPos));
        reverse = new RoadLaneTrajectory(
            reversePos,
            reverseDir,
            ComputeLengthPrefixes(reversePos));
    }

    private static RelTile3f GetExactDirection(
        TrainTrackNodeDirection direction)
    {
        var grade = direction.GradeFactor == TrainTrackGradeFactor.G0
            ? Fix32.Zero
            : Fix32.One / (int)direction.GradeFactor;
        return new RelTile3f(
                new RelTile2f(direction.Direction.Vector2f.Normalized),
                grade)
            .Normalized;
    }

    private static EntityLayout CreateMinimalHighwayLayout(
        EntityLayout source)
    {
        // Generic layout validation does not understand the intentional
        // overlaps between adjacent pieces produced by the train planner.
        // A fully copied train footprint therefore rejects alternating road
        // pieces. One neutral occupied tile near the piece centre keeps the
        // entity valid and saveable without colliding at every seam. The
        // placement controller separately samples terrain below the active
        // profile's complete asphalt width before a plan can be committed.
        var centerX = (source.CoreMin.X + source.CoreMax.X) / 2;
        var centerY = (source.CoreMin.Y + source.CoreMax.Y) / 2;
        var selectedCoord = source.LayoutTiles.IsEmpty
            ? new RelTile2i(centerX, centerY)
            : source.LayoutTiles
                .OrderBy(tile =>
                {
                    var dx = tile.Coord.X - centerX;
                    var dy = tile.Coord.Y - centerY;
                    return dx * dx + dy * dy;
                })
                .First()
                .Coord;
        var occupied = new ThicknessIRange(0, 1);
        var tile = new LayoutTile(
            selectedCoord,
            sourceStrIndex: 0,
            occupied,
            constraint: LayoutTileConstraint.None);
        var vertices = ImmutableArray.Create(
            CreateNeutralVertex(selectedCoord, occupied),
            CreateNeutralVertex(
                new RelTile2i(selectedCoord.X + 1, selectedCoord.Y),
                occupied),
            CreateNeutralVertex(
                new RelTile2i(selectedCoord.X, selectedCoord.Y + 1),
                occupied),
            CreateNeutralVertex(
                new RelTile2i(selectedCoord.X + 1, selectedCoord.Y + 1),
                occupied));

        return new EntityLayout(
            "__GROUND_ROADS_MINIMAL_LAYOUT__",
            ImmutableArray.Create(tile),
            vertices,
            ImmutableArray<IoPortTemplate>.Empty,
            EntityLayoutParams.DEFAULT,
            collapseVerticesThreshold: int.MaxValue,
            source.OriginTile,
            (source.CoreMin, source.CoreMax, source.LayoutSize));
    }

    private static TerrainVertexRel CreateNeutralVertex(
        RelTile2i coordinate,
        ThicknessIRange occupied)
    {
        return new TerrainVertexRel(
            coordinate,
            occupied,
            LayoutTileConstraint.None,
            default,
            terrainHeight: null,
            minTerrainHeight: null,
            maxTerrainHeight: null,
            vehicleSurfaceRelHeight: null,
            contributingTiles: 1,
            lowestTileIndex: 0);
    }

    private static ImmutableArray<RelTile1f> ComputeLengthPrefixes(
        ImmutableArray<RelTile3f> positions)
    {
        var result =
            new ImmutableArrayBuilder<RelTile1f>(positions.Length);
        result[0] = RelTile1f.Zero;
        for (var index = 1; index < positions.Length; index++)
        {
            result[index] = result[index - 1] +
                (positions[index] - positions[index - 1]).Length.Tiles();
        }

        return result.GetImmutableArrayAndClear();
    }

    private static void RegisterHighwayRamp(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        string name,
        string description,
        bool isOnRamp,
        ImmutableArray<ToolbarEntryData> categories,
        bool hasFixedWorldDirection = false)
    {
        // Layout entities rotate only in 90° steps, while the train planner
        // exposes 16 headings. Register one hidden geometry per 22.5° heading
        // and let the placement controller choose the exact variant. The
        // original ID stays at heading zero for saves and the toolbar popup.
        for (var directionIndex = 0; directionIndex < 16; directionIndex++)
        {
            var variantId = directionIndex == 0
                ? id
                : new StaticEntityProto.ID(
                    id.Value + "_Dir" + directionIndex.ToString("00"));
            RegisterHighwayRampDirection(
                registrator,
                variantId,
                name,
                description,
                isOnRamp,
                directionIndex,
                directionIndex == 0
                    ? categories
                    : ImmutableArray<ToolbarEntryData>.Empty,
                hasFixedWorldDirection);
        }
    }

    private static void RegisterHighwayRampDirection(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        string name,
        string description,
        bool isOnRamp,
        int directionIndex,
        ImmutableArray<ToolbarEntryData> categories,
        bool hasFixedWorldDirection)
    {
        TrainTrackNodeDirection.TryCreateFromAngle(
            (directionIndex * 22.5).Degrees(),
            TrainTrackGradeFactor.G0,
            out var nodeDirection,
            out var directionError);
        if (!string.IsNullOrEmpty(directionError))
        {
            throw new InvalidOperationException(directionError);
        }

        var direction = nodeDirection.Direction.Vector2f.Normalized;
        var right = direction.RightOrthogonalVector;
        var rightOnGrid = SnapToHalfGrid(right);
        var simpleDirectionSteps =
            GetSimpleTrainDirectionSteps(directionIndex);
        var simpleDirection = new Vector2f(
            Fix32.FromInt(simpleDirectionSteps.X),
            Fix32.FromInt(simpleDirectionSteps.Y));
        // TrainTrackNodeDirection.Dx/Dy expose fixed-point internals (for
        // example 107/54), not grid steps. V3 uses the public planner's
        // equivalent simple slope such as 2/1, 1/1 or 1/2. V2 retains its
        // historical coordinates solely for save compatibility.
        var constructionDirection = hasFixedWorldDirection
            ? simpleDirection
            : new Vector2f(
                Fix32.FromInt(nodeDirection.Dx),
                Fix32.FromInt(nodeDirection.Dy));
        var directionLength = constructionDirection.Length;
        var longitudinalSteps = Math.Max(
            3,
            (8.0 / directionLength.ToDouble()).RoundToInt());
        var roadCenter = constructionDirection * longitudinalSteps;
        Vector2f start;
        Vector2f control1;
        Vector2f control2;
        Vector2f end;
        if (isOnRamp)
        {
            start = rightOnGrid * 4;
            // The highway graph uses the exact normalized right vector for
            // its lane offset. V3 must preserve that fractional offset so an
            // integer-only TileTransform can produce a byte-identical node.
            end = roadCenter +
                (hasFixedWorldDirection ? right : rightOnGrid);
            control1 = start + roadCenter * 0.25.ToFix32();
            control2 = end - roadCenter * 0.25.ToFix32();
        }
        else
        {
            start = hasFixedWorldDirection ? right : rightOnGrid;
            end = roadCenter + rightOnGrid * 4;
            control1 = start + roadCenter * 0.25.ToFix32();
            control2 = end - roadCenter * 0.25.ToFix32();
        }

        if (hasFixedWorldDirection)
        {
            ValidateDeterministicRampGeometry(
                id,
                isOnRamp,
                direction,
                right,
                roadCenter,
                start,
                control1,
                control2,
                end);
        }

        var strings = Proto.CreateStr(id, name, description);
        // This cardinal lane is only a schema source for a valid native
        // EntityLayout. RoadEntityProto's parser normalizes diagonal endpoint
        // tangents (2:1 becomes 107:54) and rejects them before our explicit
        // RoadLaneMetadata can be supplied. The real V3 curve and graph data
        // are constructed manually below and never pass through that parser.
        var layoutLane = isOnRamp
            ? Lane(
                Curve(
                    (0.0, -4.0),
                    (2.0, -4.0),
                    (6.0, -1.0),
                    (8.0, -1.0)),
                TerrainLane,
                BasicLane)
            : Lane(
                Curve(
                    (0.0, -1.0),
                    (2.0, -1.0),
                    (6.0, -4.0),
                    (8.0, -4.0)),
                BasicLane,
                TerrainLane);
        if (!RoadEntityProto.TryCreateProto(
                id,
                strings,
                ImmutableArray.Create(layoutLane),
                RoadEntityProtoBase.Gfx.Empty,
                new EntityLayoutParser(registrator.PrototypesDb),
                out var generated,
                out var error,
                segmentsPer10Tiles: 10))
        {
            throw new InvalidOperationException(
                $"GroundRoads failed to create highway ramp '{id}': " +
                error);
        }

        var layout = CreateMinimalHighwayLayout(generated.Layout);
        var orientation = new TileTransform(
            Tile3i.Zero,
            Rotation90.Deg0,
            isReflected: false);
        var zeroInWorld = layout.TransformPoint_RelToCenterTile(
            RelTile3f.Zero,
            orientation);
        Vector2f localShift;
        if (hasFixedWorldDirection)
        {
            // RoadEntrance connects terrain through a LayoutTile. Align the
            // manually defined lane's terrain endpoint exactly with the one
            // neutral tile instead of relying on a diagonal parser footprint.
            var terrainBeforeShift = isOnRamp ? start : end;
            var connectionTile = layout.LayoutTiles.First.Coord;
            var connectionInWorld = layout.Transform(
                    connectionTile.ExtendZ(0),
                    orientation)
                .Xy;
            var desiredTerrainWorld = new Vector2f(
                Fix32.FromInt(connectionInWorld.X),
                Fix32.FromInt(connectionInWorld.Y));
            localShift = desiredTerrainWorld -
                new Vector2f(zeroInWorld.X, zeroInWorld.Y) -
                terrainBeforeShift;
        }
        else
        {
            localShift = new Vector2f(-zeroInWorld.X, -zeroInWorld.Y);
        }
        start += localShift;
        control1 += localShift;
        control2 += localShift;
        end += localShift;
        var laneCurve = Curve(
            ToDoublePoint(start),
            ToDoublePoint(control1),
            ToDoublePoint(control2),
            ToDoublePoint(end));
        var lane = Lane(
            laneCurve,
            isOnRamp ? TerrainLane : BasicLane,
            isOnRamp ? BasicLane : TerrainLane);
        var sampler = laneCurve.GetUniformSampler(32);
        var sampleCount = Math.Max(
            8,
            (sampler.CurveLengthApprox * 2).ToIntRounded());
        var positions = new ImmutableArrayBuilder<RelTile3f>(
            sampleCount + 1);
        var directions = new ImmutableArrayBuilder<RelTile3f>(
            sampleCount + 1);
        for (var index = 0; index <= sampleCount; index++)
        {
            var t = Percent.FromRatio(index, sampleCount);
            positions[index] = new RelTile3f(
                new RelTile2f(sampler.SampleUniform(t)),
                Fix32.Zero);
            directions[index] = new RelTile3f(
                new RelTile2f(
                    sampler.SampleDerivativeUniform(t).Normalized),
                Fix32.Zero);
        }
        var exactDirection = new RelTile3f(
            new RelTile2f(direction),
            Fix32.Zero);
        directions[0] = exactDirection;
        directions[sampleCount] = exactDirection;
        var adjustedPositions = positions.GetImmutableArrayAndClear();
        var adjustedDirections = directions.GetImmutableArrayAndClear();
        var prefixes = ComputeLengthPrefixes(adjustedPositions);
        var adjustedTrajectory = new RoadLaneTrajectory(
            adjustedPositions,
            adjustedDirections,
            prefixes);
        var metadata = new RoadLaneMetadata(
            adjustedPositions.First,
            adjustedPositions.Last,
            nodeDirection,
            nodeDirection,
            isOnRamp ? TerrainLane : BasicLane,
            isOnRamp ? BasicLane : TerrainLane,
            prefixes.Last);
        var lanesData = ImmutableArray.Create(metadata);
        var lanesTrajectories = ImmutableArray.Create(adjustedTrajectory);
        var terrainPoint = isOnRamp
            ? metadata.StartPosition
            : metadata.EndPosition;
        var roadPoint = isOnRamp
            ? metadata.EndPosition
            : metadata.StartPosition;
        var roadDirection = isOnRamp
            ? metadata.EndDirection
            : metadata.StartDirection;
        var laneOffset = new RelTile3f(
            new RelTile2f(
                roadDirection.Direction.RightOrthogonalVector
                    .Vector2f.Normalized),
            Fix32.Zero);
        var roadCenterPoint = roadPoint - laneOffset;
        var terrainConnectionTile =
            FindClosestLayoutTile(layout, terrainPoint);
        if (hasFixedWorldDirection)
        {
            CheckTerrainConnectionTile(
                id,
                layout,
                terrainPoint,
                terrainConnectionTile);
        }

        var connection = new LaneTerrainConnectionSpec(
            terrainConnectionTile,
            0,
            isOnRamp);
        var gfx = new GroundRoadGfx(categories, RoadIcon);

        GroundRoadEntranceProto proto = isOnRamp
            ? new HighwayOnRampProto(
                id, strings, layout, EntityCosts.None,
                s_maxRoadVehicleSpeedPerTick, ImmutableArray.Create(lane),
                lanesData, lanesTrajectories,
                ImmutableArray.Create(connection), terrainPoint,
                roadCenterPoint,
                gfx,
                hasFixedWorldDirection ? nodeDirection : null)
            : new HighwayOffRampProto(
                id, strings, layout, EntityCosts.None,
                s_maxRoadVehicleSpeedPerTick, ImmutableArray.Create(lane),
                lanesData, lanesTrajectories,
                ImmutableArray.Create(connection), terrainPoint,
                roadCenterPoint,
                gfx,
                hasFixedWorldDirection ? nodeDirection : null);

        registrator.PrototypesDb.Add(proto);
        proto.AddParam(new DrawArrowWileBuildingProtoParam(4f));
    }

    private static void ValidateDeterministicRampGeometry(
        StaticEntityProto.ID id,
        bool isOnRamp,
        Vector2f direction,
        Vector2f right,
        Vector2f roadCenter,
        Vector2f start,
        Vector2f control1,
        Vector2f control2,
        Vector2f end)
    {
        var roadPoint = isOnRamp ? end : start;
        var terrainPoint = isOnRamp ? start : end;
        var expectedRoadPoint =
            (isOnRamp ? roadCenter : Vector2f.Zero) + right;
        var roadError = roadPoint - expectedRoadPoint;
        var roadErrorSqr =
            roadError.X.ToDouble() * roadError.X.ToDouble() +
            roadError.Y.ToDouble() * roadError.Y.ToDouble();
        if (roadErrorSqr > 0.000001)
        {
            throw new InvalidOperationException(
                $"GroundRoads ramp '{id}' does not terminate at the exact " +
                "highway lane offset.");
        }

        var terrainDelta = terrainPoint - roadPoint;
        var longitudinal =
            terrainDelta.X.ToDouble() * direction.X.ToDouble() +
            terrainDelta.Y.ToDouble() * direction.Y.ToDouble();
        var lateral =
            terrainDelta.X.ToDouble() * right.X.ToDouble() +
            terrainDelta.Y.ToDouble() * right.Y.ToDouble();
        var expectedLongitudinalSign = isOnRamp
            ? longitudinal < -1.0
            : longitudinal > 1.0;
        if (!expectedLongitudinalSign || lateral <= 0.5)
        {
            throw new InvalidOperationException(
                $"GroundRoads ramp '{id}' violates its directed " +
                $"{(isOnRamp ? "merge" : "diverge")} invariant.");
        }

        var startTangent = control1 - start;
        var endTangent = end - control2;
        var startDot =
            startTangent.X.ToDouble() * direction.X.ToDouble() +
            startTangent.Y.ToDouble() * direction.Y.ToDouble();
        var endDot =
            endTangent.X.ToDouble() * direction.X.ToDouble() +
            endTangent.Y.ToDouble() * direction.Y.ToDouble();
        if (startDot <= 0.0 || endDot <= 0.0)
        {
            throw new InvalidOperationException(
                $"GroundRoads ramp '{id}' has a reversed endpoint tangent.");
        }
    }

    private static void CheckTerrainConnectionTile(
        StaticEntityProto.ID id,
        EntityLayout layout,
        RelTile3f terrainPoint,
        RelTile2i connectionTile)
    {
        var endpointTile = layout.TransformPoint_RelToCenterTile(
                terrainPoint,
                TileTransform.Identity)
            .Xy
            .Tile2i;
        var transformedConnection = layout.Transform(
                connectionTile.ExtendZ(0),
                TileTransform.Identity)
            .Xy;
        var distance =
            Math.Abs(transformedConnection.X - endpointTile.X) +
            Math.Abs(transformedConnection.Y - endpointTile.Y);
        // Native RoadEntrance prototypes intentionally connect a lane end to
        // the nearest occupied layout tile; that tile can be several cells
        // inside the road footprint. This is diagnostic only. Treating the
        // distance as a hard geometry invariant prevented otherwise valid
        // ramps (the cardinal on-ramp is three tiles) from registering.
        if (distance > 4)
        {
            Log.Warning(
                $"GroundRoads ramp '{id}' terrain connection is " +
                $"{distance} tiles away from its lane endpoint.");
        }
    }

    private static (double X, double Y) ToDoublePoint(Vector2f point)
    {
        return (point.X.ToDouble(), point.Y.ToDouble());
    }

    private static (int X, int Y) GetSimpleTrainDirectionSteps(
        int directionIndex)
    {
        // The train planner has 16 headings but represents the intermediate
        // ones with grid-safe slopes, not literal 22.5-degree irrational
        // vectors. These pairs are the reduced ratios accepted by the native
        // road layout parser.
        return (directionIndex & 15) switch
        {
            0 => (1, 0),
            1 => (2, 1),
            2 => (1, 1),
            3 => (1, 2),
            4 => (0, 1),
            5 => (-1, 2),
            6 => (-1, 1),
            7 => (-2, 1),
            8 => (-1, 0),
            9 => (-2, -1),
            10 => (-1, -1),
            11 => (-1, -2),
            12 => (0, -1),
            13 => (1, -2),
            14 => (1, -1),
            _ => (2, -1)
        };
    }

    private static Vector2f SnapToHalfGrid(Vector2f point)
    {
        return new Vector2f(
            Fix32.FromInt((point.X * 2).ToIntRounded()) / 2,
            Fix32.FromInt((point.Y * 2).ToIntRounded()) / 2);
    }

    private static string SanitizeId(string id)
    {
        var chars = id.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return new string(chars);
    }

    private static RoadLaneSpec Lane(
        CubicBezierCurve2f trajectory,
        RoadLaneType startType,
        RoadLaneType endType)
    {
        return new RoadLaneSpec(
            trajectory,
            RelTile1f.Zero,
            FlatHeight,
            startType,
            endType);
    }

    private static void RegisterLegacyRoadTileSurface(
        ProtoRegistrator registrator)
    {
        var source = registrator.PrototypesDb
            .GetOrThrow<TerrainTileSurfaceProto>(
                Ids.TerrainTileSurfaces.ConcreteReinforced);
        var strings = Proto.CreateStr(
            GroundRoadIds.RoadTileSurface,
            GroundRoadTexts.Get("legacy.surface.name"),
            GroundRoadTexts.Get("legacy.surface.description"));
        var graphics = new TerrainTileSurfaceProto.Gfx(
            source.Graphics.TextureSpec,
            source.Graphics.EdgesSpec,
            dustinessPerc: 0.08f,
            dustColor: new ColorRgba(48, 52, 56, 32),
            customIconPath: source.IconPath);

        registrator.PrototypesDb.Add(
            new TerrainTileSurfaceProto(
                GroundRoadIds.RoadTileSurface,
                strings,
                50.Percent(),
                source.CostPerTile,
                canBePlacedByPlayer: false,
                ImmutableArray<Proto.ID>.Empty,
                graphics));
    }

    private static void RegisterAccessibleSegment(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        string name,
        string description,
        ImmutableArray<RoadLaneSpec> lanes,
        ImmutableArray<(int LaneIndex, bool IsAtStart)> connectionEnds,
        int segmentsPer10Tiles = 8)
    {
        var strings = Proto.CreateStr(id, name, description);

        if (!RoadEntityProto.TryCreateProto(
                id,
                strings,
                lanes,
                RoadEntityProtoBase.Gfx.Empty,
                new EntityLayoutParser(registrator.PrototypesDb),
                out var generated,
                out var error,
                segmentsPer10Tiles))
        {
            throw new InvalidOperationException(
                $"GroundRoads failed to create native segment '{id}': " +
                error);
        }

        if (generated.Layout.LayoutTiles.Length == 0 ||
            generated.Layout.TerrainVertices.Length == 0)
        {
            throw new InvalidOperationException(
                $"GroundRoads generated an invalid layout for '{id}'.");
        }

        var connections =
            new LaneTerrainConnectionSpec[connectionEnds.Length];
        for (var index = 0; index < connectionEnds.Length; index++)
        {
            var connection = connectionEnds[index];
            var lane = generated.LanesData[connection.LaneIndex];
            var endpoint = connection.IsAtStart
                ? lane.StartPosition
                : lane.EndPosition;
            connections[index] = new LaneTerrainConnectionSpec(
                FindClosestLayoutTile(generated.Layout, endpoint),
                connection.LaneIndex,
                connection.IsAtStart);
        }

        var segmentStart = generated.LanesData[0].StartPosition.Average(
            generated.LanesData[3].EndPosition);
        var segmentEnd = generated.LanesData[1].EndPosition.Average(
            generated.LanesData[2].StartPosition);
        var proto = registrator.PrototypesDb.Add(
            new GroundRoadAccessibleSegmentProto(
                id,
                strings,
                generated.Layout,
                EntityCosts.None,
                s_maxRoadVehicleSpeedPerTick,
                generated.LanesSpecs,
                generated.LanesData,
                generated.LanesTrajectories,
                new ImmutableArray<LaneTerrainConnectionSpec>(connections),
                segmentStart,
                segmentEnd,
                new GroundRoadGfx(
                    ImmutableArray<ToolbarEntryData>.Empty,
                    RoadIcon)));

        proto.AddParam(new DrawArrowWileBuildingProtoParam(3f));
        Log.Info(
            $"GroundRoads: native segment layout has " +
            $"{generated.Layout.LayoutTiles.Length} tiles, " +
            $"{generated.Layout.TerrainVertices.Length} terrain vertices, " +
            $"and {connections.Length} terrain connections.");
    }

    private static CubicBezierCurve2f Curve(
        (double X, double Y) start,
        (double X, double Y) control1,
        (double X, double Y) control2,
        (double X, double Y) end)
    {
        return TrainTracksData.CreateCurve(
            (start.X, start.Y),
            (control1.X, control1.Y),
            (control2.X, control2.Y),
            (end.X, end.Y));
    }

    private static void RegisterRoad(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        string name,
        string description,
        ImmutableArray<RoadLaneSpec> lanes,
        ImmutableArray<ToolbarEntryData> categories,
        int segmentsPer10Tiles = 4,
        bool makeLayoutNonBlocking = false)
    {
        var strings = Proto.CreateStr(id, name, description);

        if (!RoadEntityProto.TryCreateProto(
                id,
                strings,
                lanes,
                RoadEntityProtoBase.Gfx.Empty,
                new EntityLayoutParser(registrator.PrototypesDb),
                out var generated,
                out var error,
                segmentsPer10Tiles))
        {
            throw new InvalidOperationException(
                $"GroundRoads failed to create '{id}': {error}");
        }

        if (generated.Layout.LayoutTiles.Length == 0)
        {
            throw new InvalidOperationException(
                $"GroundRoads generated an empty layout for '{id}'.");
        }

        var layout = makeLayoutNonBlocking
            ? CreateNonBlockingLayout(generated.Layout)
            : generated.Layout;

        var proto = registrator.PrototypesDb.Add(
            new GroundRoadProto(
                id,
                strings,
                layout,
                EntityCosts.None,
                s_maxRoadVehicleSpeedPerTick,
                generated.LanesSpecs,
                generated.LanesData,
                generated.LanesTrajectories,
                ComputeRoadCenterPoint(generated.LanesData, atStart: true),
                ComputeRoadCenterPoint(generated.LanesData, atStart: false),
                new GroundRoadGfx(categories, RoadIcon)));

        proto.AddParam(new DrawArrowWileBuildingProtoParam(3f));
    }

    private static void RegisterEntrance(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        string name,
        string description,
        ImmutableArray<RoadLaneSpec> lanes,
        ImmutableArray<(int LaneIndex, bool IsAtStart)> connectionEnds,
        ImmutableArray<ToolbarEntryData> categories,
        int segmentsPer10Tiles = 8,
        bool makeLayoutNonBlocking = false)
    {
        var strings = Proto.CreateStr(id, name, description);

        if (!RoadEntityProto.TryCreateProto(
                id,
                strings,
                lanes,
                RoadEntityProtoBase.Gfx.Empty,
                new EntityLayoutParser(registrator.PrototypesDb),
                out var generated,
                out var error,
                segmentsPer10Tiles))
        {
            throw new InvalidOperationException(
                $"GroundRoads failed to create entrance '{id}': {error}");
        }

        if (generated.Layout.LayoutTiles.Length == 0)
        {
            throw new InvalidOperationException(
                $"GroundRoads generated an empty layout for entrance '{id}'.");
        }

        var connections =
            new LaneTerrainConnectionSpec[connectionEnds.Length];
        var terrainPoint = RelTile3f.Zero;
        var connectionPoint = RelTile3f.Zero;

        for (var index = 0; index < connectionEnds.Length; index++)
        {
            var connection = connectionEnds[index];
            var lane = generated.LanesData[connection.LaneIndex];
            var endpoint = connection.IsAtStart
                ? lane.StartPosition
                : lane.EndPosition;
            var oppositeEndpoint = connection.IsAtStart
                ? lane.EndPosition
                : lane.StartPosition;

            if (index == 0)
            {
                terrainPoint = endpoint;
                connectionPoint = oppositeEndpoint;
            }
            else
            {
                terrainPoint = terrainPoint.Average(endpoint);
                connectionPoint = connectionPoint.Average(oppositeEndpoint);
            }

            connections[index] = new LaneTerrainConnectionSpec(
                FindClosestLayoutTile(generated.Layout, endpoint),
                connection.LaneIndex,
                connection.IsAtStart);
        }

        var layout = makeLayoutNonBlocking
            ? CreateNonBlockingLayout(generated.Layout)
            : generated.Layout;

        var proto = registrator.PrototypesDb.Add(
            new GroundRoadEntranceProto(
                id,
                strings,
                layout,
                EntityCosts.None,
                s_maxRoadVehicleSpeedPerTick,
                generated.LanesSpecs,
                generated.LanesData,
                generated.LanesTrajectories,
                new ImmutableArray<LaneTerrainConnectionSpec>(connections),
                terrainPoint,
                connectionPoint,
                new GroundRoadGfx(categories, RoadIcon)));

        proto.AddParam(new DrawArrowWileBuildingProtoParam(3f));
    }

    private static EntityLayout CreateNonBlockingLayout(EntityLayout source)
    {
        return EntityLayout.CreateEmpty(
            source.CoreMin,
            source.CoreMax,
            source.OriginTile);
    }

    private static RelTile3f ComputeRoadCenterPoint(
        ImmutableArray<RoadLaneMetadata> lanes,
        bool atStart)
    {
        var first = atStart
            ? lanes[0].StartPosition
            : lanes[0].EndPosition;

        if (lanes.Length == 1)
        {
            return first;
        }

        var oppositeLane = atStart
            ? lanes[1].EndPosition
            : lanes[1].StartPosition;
        return first.Average(oppositeLane);
    }

    private static RelTile2i FindClosestLayoutTile(
        EntityLayout layout,
        RelTile3f endpoint)
    {
        var endpointTile =
            layout.TransformPoint_RelToCenterTile(
                    endpoint,
                    TileTransform.Identity)
                .Xy
                .Tile2i;

        var best = RelTile2i.Zero;
        var bestDistance = int.MaxValue;

        foreach (var layoutTile in layout.LayoutTiles)
        {
            var transformed =
                layout.Transform(
                        layoutTile.Coord.ExtendZ(0),
                        TileTransform.Identity)
                    .Xy;

            var distance =
                Math.Abs(transformed.X - endpointTile.X) +
                Math.Abs(transformed.Y - endpointTile.Y);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = layoutTile.Coord;
            }
        }

        return best;
    }
}
