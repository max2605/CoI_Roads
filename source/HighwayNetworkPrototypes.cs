using System;
using System.Collections.Generic;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Mods;
using Mafi.Core.Ports.Io;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Trains;
using Mafi.Curves;

namespace GroundRoads;

/// <summary>
/// Marks every directed road entity that participates in the custom highway
/// graph. This includes mainline pieces and multi-arm nodes.
/// </summary>
public interface IHighwayNetworkProto
{
    bool ParticipatesInHighwayNetwork { get; }
}

/// <summary>
/// Marks road entities that form the actual highway network.
/// </summary>
public interface IHighwayMainlineProto : IHighwayNetworkProto
{
}

/// <summary>
/// Exposes every physical two-way connection of a highway entity. Road graph
/// keys include position, heading, and lane type, so both directed nodes are
/// carried and compared exactly.
/// </summary>
public interface IHighwayPortProto : IHighwayMainlineProto
{
    int HighwayPortCount { get; }

    HighwayPort GetHighwayPort(int index, TileTransform transform);
}

public readonly struct HighwayPort
{
    public const int DefaultAttachmentCollisionSeamRange = 2;
    public const int RoundaboutAttachmentCollisionSeamRange = 4;

    public readonly Tile3i Center;
    public readonly RoadGraphNodeKey InboundNode;
    public readonly RoadGraphNodeKey OutboundNode;
    public readonly Tile3i SnapAnchor;
    public readonly bool HasExtendedSnapArea;
    public readonly int AttachmentCollisionSeamRange;

    public HighwayPort(
        Tile3i center,
        RoadGraphNodeKey inboundNode,
        RoadGraphNodeKey outboundNode,
        Tile3i? extendedSnapAnchor = null,
        int attachmentCollisionSeamRange =
            DefaultAttachmentCollisionSeamRange)
    {
        Center = center;
        InboundNode = inboundNode;
        OutboundNode = outboundNode;
        SnapAnchor = extendedSnapAnchor ?? center;
        HasExtendedSnapArea = extendedSnapAnchor.HasValue;
        AttachmentCollisionSeamRange = attachmentCollisionSeamRange;
    }

    public bool IsExactMateOf(HighwayPort other)
    {
        return InboundNode == other.OutboundNode &&
               OutboundNode == other.InboundNode &&
               Center == other.Center;
    }
}

internal readonly struct HighwayPortKey : IEquatable<HighwayPortKey>
{
    private readonly RoadGraphNodeKey m_first;
    private readonly RoadGraphNodeKey m_second;

    public HighwayPortKey(HighwayPort port)
    {
        m_first = port.InboundNode;
        m_second = port.OutboundNode;
    }

    public bool Equals(HighwayPortKey other)
    {
        return (m_first == other.m_first && m_second == other.m_second) ||
               (m_first == other.m_second && m_second == other.m_first);
    }

    public override bool Equals(object obj)
    {
        return obj is HighwayPortKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return m_first.GetHashCode() ^ m_second.GetHashCode();
    }
}

public enum HighwayNodeKind
{
    TIntersection,
    CrossIntersection,
    Roundabout
}

public readonly struct HighwayPortSpec
{
    public readonly RelTile3f Center;
    public readonly int InboundLaneIndex;
    public readonly int OutboundLaneIndex;

    public HighwayPortSpec(
        RelTile3f center,
        int inboundLaneIndex,
        int outboundLaneIndex)
    {
        Center = center;
        InboundLaneIndex = inboundLaneIndex;
        OutboundLaneIndex = outboundLaneIndex;
    }
}

/// <summary>
/// A native multi-lane road entity used for T/+ intersections and the
/// roundabout. Logical turn lanes are independent from the small collection of
/// visual road-center trajectories to avoid rendering the same asphalt twelve
/// times.
/// </summary>
public sealed class HighwayJunctionProto : RoadEntityProto,
    IHighwayPortProto
{
    private readonly ImmutableArray<HighwayPortSpec> m_ports;

    public HighwayNodeKind Kind { get; }

    public int BaseDirectionIndex { get; }

    public bool ParticipatesInHighwayNetwork => true;

    public ImmutableArray<RoadLaneTrajectory> VisualRoadTrajectories { get; }

    public int HighwayPortCount => m_ports.Length;

    public HighwayJunctionProto(
        StaticEntityProto.ID id,
        Proto.Str strings,
        EntityLayout layout,
        EntityCosts costs,
        RelTile1f maxVehiclesSpeedPerTick,
        ImmutableArray<RoadLaneSpec> lanesSpecs,
        ImmutableArray<RoadLaneMetadata> lanesData,
        ImmutableArray<RoadLaneTrajectory> lanesTrajectories,
        ImmutableArray<HighwayPortSpec> ports,
        ImmutableArray<RoadLaneTrajectory> visualRoadTrajectories,
        HighwayNodeKind kind,
        int baseDirectionIndex,
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
        m_ports = ports;
        VisualRoadTrajectories = visualRoadTrajectories;
        Kind = kind;
        BaseDirectionIndex = baseDirectionIndex;
    }

    public HighwayPort GetHighwayPort(int index, TileTransform transform)
    {
        if (index < 0 || index >= m_ports.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var port = m_ports[index];
        var center = Layout.TransformPoint_RelToCenterTile(
                port.Center,
                transform)
            .Tile3iRounded;
        var snapAnchor = Layout.TransformPoint_RelToCenterTile(
                RelTile3f.Zero,
                transform)
            .Tile3iRounded;
        return new HighwayPort(
            center,
            GetTransformedStartGraphNode(
                port.InboundLaneIndex,
                transform),
            GetTransformedEndGraphNode(
                port.OutboundLaneIndex,
                transform),
            snapAnchor,
            Kind == HighwayNodeKind.Roundabout
                ? HighwayPort.RoundaboutAttachmentCollisionSeamRange
                : HighwayPort.DefaultAttachmentCollisionSeamRange);
    }
}

internal static class HighwayNetworkData
{
    private const int RoundaboutRadius = 4;
    private const int SamplesPerQuarterCircle = 8;
    private const string RoadIcon =
        "Assets/Unity/Generated/Icons/Vehicle/TruckT2.png";

    private const RoadLaneType BasicLane =
        RoadLaneType.MaskAllowAll | RoadLaneType.BasicLaneFlag;

    private static readonly CubicBezierCurve2f FlatHeight =
        Curve((0.0, 0.0), (0.25, 0.0), (0.75, 0.0), (1.0, 0.0));

    private readonly struct Arm
    {
        public readonly int DirectionIndex;
        public readonly Vector2f Center;
        public readonly Vector2f OutwardVector;
        public readonly TrainTrackNodeDirection OutwardDirection;

        public Arm(int directionIndex)
        {
            DirectionIndex = directionIndex & 15;
            if (!TrainTrackNodeDirection.TryCreateFromAngle(
                    (DirectionIndex * 22.5).Degrees(),
                    TrainTrackGradeFactor.G0,
                    out var direction,
                    out var error))
            {
                throw new InvalidOperationException(error);
            }

            OutwardDirection = direction;
            OutwardVector = direction.Direction.Vector2f.Normalized;
            // Highway ports are centred on integer tiles. Lane offsets still
            // use the exact 22.5-degree direction, while this integral centre
            // lets both directed graph keys mate byte-for-byte.
            Center = GetArmCenter(DirectionIndex);
        }

        public Vector2f Right => OutwardVector.RightOrthogonalVector;

        public Vector2f InboundPosition => Center - Right;

        public Vector2f OutboundPosition => Center + Right;
    }

    private sealed class NodeGeometry
    {
        public readonly List<RoadLaneSpec> LaneSpecs = new();
        public readonly List<RoadLaneMetadata> LaneData = new();
        public readonly List<RoadLaneTrajectory> LaneTrajectories = new();
        public readonly List<HighwayPortSpec> Ports = new();
        public readonly List<RoadLaneTrajectory> VisualTrajectories = new();
    }

    public static void Register(
        ProtoRegistrator registrator,
        ToolbarCategoryProto roadsCategory,
        RelTile1f maxVehiclesSpeedPerTick)
    {
        _ = roadsCategory;
        RegisterNodeVariants(
            registrator,
            GroundRoadIds.HighwayTIntersection,
            "Autobahn-T-Kreuzung",
            "Ungeregelte dreiseitige Autobahnkreuzung mit allen sechs " +
            "gerichteten Abbiegemöglichkeiten.",
            HighwayNodeKind.TIntersection,
            CreateTIntersectionVariant,
            maxVehiclesSpeedPerTick);
        RegisterNodeVariants(
            registrator,
            GroundRoadIds.HighwayCrossIntersection,
            "Autobahn-Kreuzung",
            "Ungeregelte vierseitige Autobahnkreuzung mit Geradeaus-, " +
            "Links- und Rechtsabbiegern.",
            HighwayNodeKind.CrossIntersection,
            CreateCrossIntersectionVariant,
            maxVehiclesSpeedPerTick);
        RegisterNodeVariants(
            registrator,
            GroundRoadIds.HighwayRoundabout,
            "Autobahn-Kreisverkehr",
            "Vierarmiger Kreisverkehr für Rechtsverkehr. Jede Zufahrt kann " +
            "jede der drei anderen Ausfahrten erreichen.",
            HighwayNodeKind.Roundabout,
            CreateRoundaboutVariant,
            maxVehiclesSpeedPerTick);
    }

    private static void RegisterNodeVariants(
        ProtoRegistrator registrator,
        StaticEntityProto.ID baseId,
        string name,
        string description,
        HighwayNodeKind kind,
        Func<int, NodeGeometry> geometryFactory,
        RelTile1f maxVehiclesSpeedPerTick)
    {
        // TileTransform contributes four 90-degree rotations. Four base
        // geometries offset by 0/22.5/45/67.5 degrees cover all 16 planner
        // headings without approximating their graph-node directions.
        for (var baseDirectionIndex = 0;
             baseDirectionIndex < 4;
             baseDirectionIndex++)
        {
            var id = baseDirectionIndex == 0
                ? baseId
                : new StaticEntityProto.ID(
                    $"{baseId.Value}_Dir{baseDirectionIndex:00}");
            RegisterNode(
                registrator,
                id,
                name,
                description,
                kind,
                baseDirectionIndex,
                geometryFactory(baseDirectionIndex),
                maxVehiclesSpeedPerTick);
        }
    }

    private static void RegisterNode(
        ProtoRegistrator registrator,
        StaticEntityProto.ID id,
        string name,
        string description,
        HighwayNodeKind kind,
        int baseDirectionIndex,
        NodeGeometry geometry,
        RelTile1f maxVehiclesSpeedPerTick)
    {
        ValidateGeometry(id, geometry, kind);
        var layout = CreateJunctionLayout();
        var proto = registrator.PrototypesDb.Add(
            new HighwayJunctionProto(
                id,
                Proto.CreateStr(id, name, description),
                layout,
                HighwayConstructionCosts.CreateForNode(registrator, kind),
                maxVehiclesSpeedPerTick,
                ToImmutable(geometry.LaneSpecs),
                ToImmutable(geometry.LaneData),
                ToImmutable(geometry.LaneTrajectories),
                ToImmutable(geometry.Ports),
                ToImmutable(geometry.VisualTrajectories),
                kind,
                baseDirectionIndex,
                new GroundRoadGfx(
                    ImmutableArray<ToolbarEntryData>.Empty,
                    RoadIcon)));
        proto.AddParam(new DrawArrowWileBuildingProtoParam(3f));

        Log.Info(
            $"GroundRoads: registered {kind} with " +
            $"{geometry.Ports.Count} ports and " +
            $"{geometry.LaneData.Count} directed movements.");
    }

    private static EntityLayout CreateJunctionLayout()
    {
        const int min = -3;
        const int max = 2;
        var occupied = new ThicknessIRange(0, 1);
        var tiles = new List<LayoutTile>();
        for (var x = min; x <= max; x++)
        {
            for (var y = min; y <= max; y++)
            {
                tiles.Add(new LayoutTile(
                    new RelTile2i(x, y),
                    sourceStrIndex: 0,
                    occupied,
                    constraint: LayoutTileConstraint.None));
            }
        }

        var vertices = new List<TerrainVertexRel>();
        for (var x = min; x <= max + 1; x++)
        {
            for (var y = min; y <= max + 1; y++)
            {
                vertices.Add(CreateNeutralVertex(
                    new RelTile2i(x, y),
                    occupied));
            }
        }

        return new EntityLayout(
            "__GROUND_ROADS_JUNCTION_LAYOUT__",
            ToImmutable(tiles),
            ToImmutable(vertices),
            ImmutableArray<IoPortTemplate>.Empty,
            EntityLayoutParams.DEFAULT,
            collapseVerticesThreshold: int.MaxValue,
            originTile: null,
            sizeOverride: (
                new RelTile2i(min, min),
                new RelTile2i(max, max),
                new RelTile3i(
                    max - min + 1,
                    max - min + 1,
                    1)));
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

    private static TrainTrackNodeDirection CreateDirection(
        int directionIndex)
    {
        if (!TrainTrackNodeDirection.TryCreateFromAngle(
                ((directionIndex & 15) * 22.5).Degrees(),
                TrainTrackGradeFactor.G0,
                out var direction,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        return direction;
    }

    private static Vector2f GetArmCenter(int directionIndex)
    {
        var coordinates = (directionIndex & 15) switch
        {
            0 => (8, 0),
            1 => (8, 4),
            2 => (6, 6),
            3 => (4, 8),
            4 => (0, 8),
            5 => (-4, 8),
            6 => (-6, 6),
            7 => (-8, 4),
            8 => (-8, 0),
            9 => (-8, -4),
            10 => (-6, -6),
            11 => (-4, -8),
            12 => (0, -8),
            13 => (4, -8),
            14 => (6, -6),
            _ => (8, -4)
        };
        return new Vector2f(
            coordinates.Item1.ToFix32(),
            coordinates.Item2.ToFix32());
    }

    private static NodeGeometry CreateTIntersection()
    {
        return CreateTIntersectionVariant(0);
    }

    private static NodeGeometry CreateTIntersectionVariant(
        int baseDirectionIndex)
    {
        return CreateDirectNode(
            new[]
            {
                new Arm(baseDirectionIndex),
                new Arm(baseDirectionIndex + 4),
                new Arm(baseDirectionIndex + 8)
            });
    }

    private static NodeGeometry CreateCrossIntersection()
    {
        return CreateCrossIntersectionVariant(0);
    }

    private static NodeGeometry CreateCrossIntersectionVariant(
        int baseDirectionIndex)
    {
        return CreateDirectNode(
            new[]
            {
                new Arm(baseDirectionIndex),
                new Arm(baseDirectionIndex + 4),
                new Arm(baseDirectionIndex + 8),
                new Arm(baseDirectionIndex + 12)
            });
    }

    private static NodeGeometry CreateDirectNode(Arm[] arms)
    {
        var geometry = new NodeGeometry();
        var inboundIndices = CreateIndexArray(arms.Length);
        var outboundIndices = CreateIndexArray(arms.Length);

        for (var fromIndex = 0; fromIndex < arms.Length; fromIndex++)
        {
            for (var toIndex = 0; toIndex < arms.Length; toIndex++)
            {
                if (fromIndex == toIndex)
                {
                    continue;
                }

                var from = arms[fromIndex];
                var to = arms[toIndex];
                var start = from.InboundPosition;
                var end = to.OutboundPosition;
                var startDirection = -from.OutwardVector;
                var endDirection = to.OutwardVector;
                var controlDistance = 5.ToFix32();
                var control1 = start + startDirection * controlDistance;
                var control2 = end - endDirection * controlDistance;
                var curve = Curve(
                    ToDoublePoint(start),
                    ToDoublePoint(control1),
                    ToDoublePoint(control2),
                    ToDoublePoint(end));
                var trajectory = CreateCurveTrajectory(
                    curve,
                    from.OutwardDirection.Inversed(),
                    to.OutwardDirection,
                    sampleCount: 20);
                var laneIndex = geometry.LaneData.Count;

                geometry.LaneSpecs.Add(Lane(curve));
                geometry.LaneTrajectories.Add(trajectory);
                geometry.LaneData.Add(CreateMetadata(
                    trajectory,
                    from.OutwardDirection.Inversed(),
                    to.OutwardDirection));
                if (inboundIndices[fromIndex] < 0)
                {
                    inboundIndices[fromIndex] = laneIndex;
                }

                if (outboundIndices[toIndex] < 0)
                {
                    outboundIndices[toIndex] = laneIndex;
                }
            }
        }

        AddPorts(geometry, arms, inboundIndices, outboundIndices);
        foreach (var arm in arms)
        {
            geometry.VisualTrajectories.Add(CreateArmVisual(arm));
        }

        return geometry;
    }

    private static NodeGeometry CreateRoundabout()
    {
        return CreateRoundaboutVariant(0);
    }

    private static NodeGeometry CreateRoundaboutVariant(
        int baseDirectionIndex)
    {
        var arms = new[]
        {
            new Arm(baseDirectionIndex),
            new Arm(baseDirectionIndex + 4),
            new Arm(baseDirectionIndex + 8),
            new Arm(baseDirectionIndex + 12)
        };
        var geometry = new NodeGeometry();
        var inboundIndices = CreateIndexArray(arms.Length);
        var outboundIndices = CreateIndexArray(arms.Length);

        // Each physical section exists exactly once. Vehicles entering from
        // different arms therefore share the same four ring lanes and can see
        // one another through the native road occupancy model.
        for (var index = 0; index < arms.Length; index++)
        {
            var arm = arms[index];
            var ringNodeDirectionIndex =
                baseDirectionIndex + 2 + index * 4;
            var ringRadial = CreateDirection(ringNodeDirectionIndex)
                .Direction.Vector2f.Normalized;
            var ringPosition = ringRadial * RoundaboutRadius;
            var ringDirection = CreateDirection(
                ringNodeDirectionIndex + 4);
            var ringTangent =
                ringDirection.Direction.Vector2f.Normalized;
            var entryCurve = Curve(
                ToDoublePoint(arm.InboundPosition),
                ToDoublePoint(
                    arm.InboundPosition - arm.OutwardVector * 3),
                ToDoublePoint(ringPosition - ringTangent * 2),
                ToDoublePoint(ringPosition));
            inboundIndices[index] = AddLane(
                geometry,
                entryCurve,
                CreateCurveTrajectory(
                    entryCurve,
                    arm.OutwardDirection.Inversed(),
                    ringDirection,
                    sampleCount: 8),
                arm.OutwardDirection.Inversed(),
                ringDirection);
        }

        for (var index = 0; index < arms.Length; index++)
        {
            var startNodeDirectionIndex =
                baseDirectionIndex + 2 + index * 4;
            var endNodeDirectionIndex = startNodeDirectionIndex + 4;
            var startRadial = CreateDirection(startNodeDirectionIndex)
                .Direction.Vector2f.Normalized;
            var endRadial = CreateDirection(endNodeDirectionIndex)
                .Direction.Vector2f.Normalized;
            var startDirection = CreateDirection(
                startNodeDirectionIndex + 4);
            var endDirection = CreateDirection(
                endNodeDirectionIndex + 4);
            var start = startRadial * RoundaboutRadius;
            var end = endRadial * RoundaboutRadius;
            var startTangent =
                startDirection.Direction.Vector2f.Normalized;
            var endTangent = endDirection.Direction.Vector2f.Normalized;
            var controlDistance =
                (RoundaboutRadius * 0.5522847498307936).ToFix32();
            var ringCurve = Curve(
                ToDoublePoint(start),
                ToDoublePoint(start + startTangent * controlDistance),
                ToDoublePoint(end - endTangent * controlDistance),
                ToDoublePoint(end));
            AddLane(
                geometry,
                ringCurve,
                CreateCurveTrajectory(
                    ringCurve,
                    startDirection,
                    endDirection,
                    sampleCount: SamplesPerQuarterCircle),
                startDirection,
                endDirection);
        }

        for (var index = 0; index < arms.Length; index++)
        {
            var arm = arms[index];
            var ringNodeIndex = (index + 3) & 3;
            var ringNodeDirectionIndex =
                baseDirectionIndex + 2 + ringNodeIndex * 4;
            var ringRadial = CreateDirection(ringNodeDirectionIndex)
                .Direction.Vector2f.Normalized;
            var ringPosition = ringRadial * RoundaboutRadius;
            var ringDirection = CreateDirection(
                ringNodeDirectionIndex + 4);
            var ringTangent =
                ringDirection.Direction.Vector2f.Normalized;
            var exitCurve = Curve(
                ToDoublePoint(ringPosition),
                ToDoublePoint(ringPosition + ringTangent * 2),
                ToDoublePoint(
                    arm.OutboundPosition - arm.OutwardVector * 3),
                ToDoublePoint(arm.OutboundPosition));
            outboundIndices[index] = AddLane(
                geometry,
                exitCurve,
                CreateCurveTrajectory(
                    exitCurve,
                    ringDirection,
                    arm.OutwardDirection,
                    sampleCount: 8),
                ringDirection,
                arm.OutwardDirection);
        }

        AddPorts(geometry, arms, inboundIndices, outboundIndices);
        return geometry;
    }

    private static int AddLane(
        NodeGeometry geometry,
        CubicBezierCurve2f schemaCurve,
        RoadLaneTrajectory trajectory,
        TrainTrackNodeDirection startDirection,
        TrainTrackNodeDirection endDirection)
    {
        var laneIndex = geometry.LaneData.Count;
        geometry.LaneSpecs.Add(Lane(schemaCurve));
        geometry.LaneTrajectories.Add(trajectory);
        geometry.VisualTrajectories.Add(trajectory);
        geometry.LaneData.Add(CreateMetadata(
            trajectory,
            startDirection,
            endDirection));
        return laneIndex;
    }

    private static void AddPorts(
        NodeGeometry geometry,
        Arm[] arms,
        int[] inboundIndices,
        int[] outboundIndices)
    {
        for (var index = 0; index < arms.Length; index++)
        {
            geometry.Ports.Add(new HighwayPortSpec(
                ToRelPosition(arms[index].Center),
                inboundIndices[index],
                outboundIndices[index]));
        }
    }

    private static int[] CreateIndexArray(int length)
    {
        var result = new int[length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = -1;
        }

        return result;
    }

    private static RoadLaneTrajectory CreateCurveTrajectory(
        CubicBezierCurve2f curve,
        TrainTrackNodeDirection startDirection,
        TrainTrackNodeDirection endDirection,
        int sampleCount)
    {
        var positions = new List<RelTile3f>(sampleCount + 1);
        var directions = new List<RelTile3f>(sampleCount + 1);
        AppendCurve(
            curve,
            sampleCount,
            includeFirst: true,
            positions,
            directions);
        directions[0] = ToRelDirection(
            startDirection.Direction.Vector2f.Normalized);
        directions[directions.Count - 1] = ToRelDirection(
            endDirection.Direction.Vector2f.Normalized);
        return CreateTrajectory(positions, directions);
    }

    private static void AppendCurve(
        CubicBezierCurve2f curve,
        int sampleCount,
        bool includeFirst,
        List<RelTile3f> positions,
        List<RelTile3f> directions)
    {
        var sampler = curve.GetUniformSampler(32);
        var first = includeFirst ? 0 : 1;
        for (var index = first; index <= sampleCount; index++)
        {
            var t = Percent.FromRatio(index, sampleCount);
            positions.Add(ToRelPosition(sampler.SampleUniform(t)));
            directions.Add(ToRelDirection(
                sampler.SampleDerivativeUniform(t).Normalized));
        }
    }

    private static RoadLaneTrajectory CreateArmVisual(Arm arm)
    {
        var start = Vector2f.Zero;
        var curve = Curve(
            ToDoublePoint(start),
            ToDoublePoint(arm.OutwardVector * 2),
            ToDoublePoint(arm.Center - arm.OutwardVector * 2),
            ToDoublePoint(arm.Center));
        return CreateCurveTrajectory(
            curve,
            arm.OutwardDirection,
            arm.OutwardDirection,
            sampleCount: 12);
    }

    private static RoadLaneTrajectory CreateTrajectory(
        List<RelTile3f> positions,
        List<RelTile3f> directions)
    {
        var positionArray = ToImmutable(positions);
        return new RoadLaneTrajectory(
            positionArray,
            ToImmutable(directions),
            ComputeLengthPrefixes(positionArray));
    }

    private static RoadLaneMetadata CreateMetadata(
        RoadLaneTrajectory trajectory,
        TrainTrackNodeDirection startDirection,
        TrainTrackNodeDirection endDirection)
    {
        return new RoadLaneMetadata(
            trajectory.LaneCenterSamples.First,
            trajectory.LaneCenterSamples.Last,
            startDirection,
            endDirection,
            BasicLane,
            BasicLane,
            trajectory.SegmentLengthsPrefixSums.Last);
    }

    private static ImmutableArray<RelTile1f> ComputeLengthPrefixes(
        ImmutableArray<RelTile3f> positions)
    {
        var result = new ImmutableArrayBuilder<RelTile1f>(positions.Length);
        result[0] = RelTile1f.Zero;
        for (var index = 1; index < positions.Length; index++)
        {
            result[index] = result[index - 1] +
                (positions[index] - positions[index - 1]).Length.Tiles();
        }

        return result.GetImmutableArrayAndClear();
    }

    private static void ValidateGeometry(
        StaticEntityProto.ID id,
        NodeGeometry geometry,
        HighwayNodeKind kind)
    {
        var expectedPorts = kind == HighwayNodeKind.TIntersection ? 3 : 4;
        var expectedLanes = expectedPorts * (expectedPorts - 1);
        if (geometry.Ports.Count != expectedPorts ||
            geometry.LaneData.Count != expectedLanes ||
            geometry.LaneSpecs.Count != expectedLanes ||
            geometry.LaneTrajectories.Count != expectedLanes)
        {
            throw new InvalidOperationException(
                $"GroundRoads node '{id}' has an invalid movement matrix.");
        }

        for (var index = 0; index < geometry.LaneData.Count; index++)
        {
            var lane = geometry.LaneData[index];
            var chord = lane.StartPosition.Xy.DistanceTo(lane.EndPosition.Xy);
            if (chord > 20.Tiles().Value)
            {
                throw new InvalidOperationException(
                    $"GroundRoads node '{id}' lane {index} exceeds the " +
                    "safe 20-tile endpoint chord.");
            }
        }
    }

    private static RoadLaneSpec Lane(CubicBezierCurve2f trajectory)
    {
        return new RoadLaneSpec(
            trajectory,
            RelTile1f.Zero,
            FlatHeight,
            BasicLane,
            BasicLane);
    }

    private static RelTile3f ToRelPosition(Vector2f point)
    {
        return new RelTile3f(new RelTile2f(point), Fix32.Zero);
    }

    private static RelTile3f ToRelDirection(Vector2f direction)
    {
        return new RelTile3f(
            new RelTile2f(direction.Normalized),
            Fix32.Zero);
    }

    private static (double X, double Y) ToDoublePoint(Vector2f point)
    {
        return (point.X.ToDouble(), point.Y.ToDouble());
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

    private static ImmutableArray<T> ToImmutable<T>(List<T> values)
    {
        var result = new ImmutableArrayBuilder<T>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            result[index] = values[index];
        }

        return result.GetImmutableArrayAndClear();
    }
}
