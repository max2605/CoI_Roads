using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.PathFinding;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Trains;
using Mafi.Curves;

namespace GroundRoads.GeometrySmoke;

internal static class Program
{
    private static int Main()
    {
        try
        {
            var assembly = typeof(GroundRoadsMod).Assembly;
            var dataType = assembly.GetType(
                "GroundRoads.HighwayNetworkData",
                throwOnError: true);
            var kindType = assembly.GetType(
                "GroundRoads.HighwayNodeKind",
                throwOnError: true);
            var validate = dataType.GetMethod(
                "ValidateGeometry",
                BindingFlags.Static | BindingFlags.NonPublic);
            var idType = Type.GetType(
                "Mafi.Core.Entities.Static.StaticEntityProto+ID, Mafi.Core",
                throwOnError: true);

            var coveredDirections = new HashSet<TrainTrackNodeDirection>();
            for (var baseDirectionIndex = 0;
                 baseDirectionIndex < 4;
                 baseDirectionIndex++)
            {
                CheckGeometry(
                    dataType,
                    validate,
                    idType,
                    kindType,
                    "CreateTIntersectionVariant",
                    "TIntersection",
                    baseDirectionIndex,
                    expectedPorts: 3,
                    expectedLanes: 6,
                    checkRoundaboutTopology: false,
                    coveredDirections);
                CheckGeometry(
                    dataType,
                    validate,
                    idType,
                    kindType,
                    "CreateCrossIntersectionVariant",
                    "CrossIntersection",
                    baseDirectionIndex,
                    expectedPorts: 4,
                    expectedLanes: 12,
                    checkRoundaboutTopology: false,
                    coveredDirections);
                CheckGeometry(
                    dataType,
                    validate,
                    idType,
                    kindType,
                    "CreateRoundaboutVariant",
                    "Roundabout",
                    baseDirectionIndex,
                    expectedPorts: 4,
                    expectedLanes: 12,
                    checkRoundaboutTopology: true,
                    coveredDirections);
            }

            Require(coveredDirections.Count == 16,
                "Transformed physical ports must cover all 16 " +
                "train-planner headings.");
            CheckCollisionLayouts(assembly);
            CheckLegacyRampCompatibility(assembly);
            CheckLaneProjectionScope(assembly);
            CheckJunctionSnapPolicy(assembly);
            CheckHighwayReroutePolicy(assembly);
            CheckJunctionSpacingPolicy(assembly);
            CheckElevationRampSupport(assembly);
            CheckHighwaySupportModel(assembly);
            CheckHighwayConstructionEconomy(assembly, kindType);

            Console.WriteLine(
                "Geometry smoke checks passed: 4 variants each; " +
                "T=6, +=12, roundabout=12 shared lanes, 16 headings with " +
                "exact port mates, external-only terrain access, buffered " +
                "junction placement with a 24-tile exclusion zone, native " +
                "junction steering, no legacy ramp tools, reversible height " +
                "curves, normalized terrain ramps, grade-independent " +
                "heading selection, junction-body road snapping, extended " +
                "roundabout attachment seams, bounded terrain-reroute " +
                "work, 16 " +
                "flat-end retries, four-tile terrain " +
                "and entity-corridor clearance, geometric existing-highway " +
                "collision checks, support-capable G0/G4/G8 surfaces, " +
                "non-buried plateau crests, native Q/E elevation limits, " +
                "rail-spaced concrete supports, 24-tile directional start " +
                "snapping, " +
                "selected open-port " +
                "seams, stable asphalt IDs, deadlock-free G4/G8 pieces, " +
                "length/area-based gravel and asphalt construction costs, and " +
                "half-tile terrain access.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckGeometry(
        Type dataType,
        MethodInfo validate,
        Type idType,
        Type kindType,
        string factoryName,
        string kindName,
        int baseDirectionIndex,
        int expectedPorts,
        int expectedLanes,
        bool checkRoundaboutTopology,
        HashSet<TrainTrackNodeDirection> coveredDirections)
    {
        var factory = dataType.GetMethod(
            factoryName,
            BindingFlags.Static | BindingFlags.NonPublic);
        var geometry = factory.Invoke(
            null,
            new object[] { baseDirectionIndex });
        var geometryType = geometry.GetType();
        var ports = (ICollection)geometryType.GetField("Ports").GetValue(geometry);
        var laneData =
            (ICollection)geometryType.GetField("LaneData").GetValue(geometry);
        var laneSpecs =
            (ICollection)geometryType.GetField("LaneSpecs").GetValue(geometry);
        var trajectories =
            (ICollection)geometryType.GetField("LaneTrajectories")
                .GetValue(geometry);

        Require(ports.Count == expectedPorts,
            $"{kindName}: expected {expectedPorts} ports, got {ports.Count}.");
        Require(laneData.Count == expectedLanes,
            $"{kindName}: expected {expectedLanes} lanes, got {laneData.Count}.");
        Require(laneSpecs.Count == expectedLanes,
            $"{kindName}: lane spec count differs from movement count.");
        Require(trajectories.Count == expectedLanes,
            $"{kindName}: trajectory count differs from movement count.");

        foreach (var port in ports)
        {
            var portType = port.GetType();
            var inbound = (int)portType.GetField("InboundLaneIndex")
                .GetValue(port);
            var outbound = (int)portType.GetField("OutboundLaneIndex")
                .GetValue(port);
            Require(inbound >= 0 && inbound < expectedLanes,
                $"{kindName}: invalid inbound representative lane {inbound}.");
            Require(outbound >= 0 && outbound < expectedLanes,
                $"{kindName}: invalid outbound representative lane {outbound}.");
        }

        if (checkRoundaboutTopology)
        {
            CheckRoundaboutTopology(laneData);
        }

        CheckVisualCoverage(
            trajectories,
            (ICollection)geometryType.GetField("VisualTrajectories")
                .GetValue(geometry),
            kindName,
            baseDirectionIndex,
            checkRoundaboutTopology ? 1.0 : 4.0);

        var id = Activator.CreateInstance(
            idType,
            new object[]
            {
                "GeometrySmoke_" + kindName + "_" + baseDirectionIndex
            });
        var kind = Enum.Parse(kindType, kindName);
        validate.Invoke(null, new[] { id, geometry, kind });
        CheckPortMating(
            dataType,
            geometry,
            (HighwayNodeKind)kind,
            baseDirectionIndex,
            kindName,
            coveredDirections);
    }

    private static void CheckPortMating(
        Type dataType,
        object geometry,
        HighwayNodeKind kind,
        int baseDirectionIndex,
        string kindName,
        HashSet<TrainTrackNodeDirection> coveredDirections)
    {
        var geometryType = geometry.GetType();
        var createLayout = dataType.GetMethod(
            "CreateJunctionLayout",
            BindingFlags.Static | BindingFlags.NonPublic);
        var layout = (EntityLayout)createLayout.Invoke(
            null,
            Array.Empty<object>());
        var id = new StaticEntityProto.ID(
            $"GeometrySmokePort_{kindName}_{baseDirectionIndex}");
        var proto = new HighwayJunctionProto(
            id,
            Proto.CreateStr(id, kindName, "geometry smoke"),
            layout,
            EntityCosts.None,
            1.0.Tiles(),
            ToImmutable<RoadLaneSpec>(
                geometryType.GetField("LaneSpecs").GetValue(geometry)),
            ToImmutable<RoadLaneMetadata>(
                geometryType.GetField("LaneData").GetValue(geometry)),
            ToImmutable<RoadLaneTrajectory>(
                geometryType.GetField("LaneTrajectories").GetValue(geometry)),
            ToImmutable<HighwayPortSpec>(
                geometryType.GetField("Ports").GetValue(geometry)),
            ToImmutable<RoadLaneTrajectory>(
                geometryType.GetField("VisualTrajectories")
                    .GetValue(geometry)),
            kind,
            baseDirectionIndex,
            RoadEntityProtoBase.Gfx.Empty);
        CheckTerrainAccessScope(
            dataType.Assembly,
            proto,
            kindName,
            baseDirectionIndex);
        var rotations = new[]
        {
            Rotation90.Deg0,
            Rotation90.Deg90,
            Rotation90.Deg180,
            Rotation90.Deg270
        };

        for (var rotationIndex = 0;
             rotationIndex < rotations.Length;
             rotationIndex++)
        {
            var rotation = rotations[rotationIndex];
            var oppositeRotation = rotations[(rotationIndex + 2) & 3];
            var primaryOrientation = new TileTransform(
                Tile3i.Zero,
                rotation,
                isReflected: false);
            var oppositeTransform = new TileTransform(
                Tile3i.Zero,
                oppositeRotation,
                isReflected: false);
            for (var portIndex = 0;
                 portIndex < proto.HighwayPortCount;
                 portIndex++)
            {
                var primaryRelative = proto.GetHighwayPort(
                    portIndex,
                    primaryOrientation);
                Require(primaryRelative.HasExtendedSnapArea,
                    $"{kindName} base {baseDirectionIndex}, rotation " +
                    $"{rotationIndex}, port {portIndex} must expose its " +
                    "junction body as an extended road snap target.");
                var expectedAttachmentSeam = kindName == "Roundabout"
                    ? HighwayPort.RoundaboutAttachmentCollisionSeamRange
                    : HighwayPort.DefaultAttachmentCollisionSeamRange;
                Require(primaryRelative.AttachmentCollisionSeamRange ==
                        expectedAttachmentSeam,
                    $"{kindName} base {baseDirectionIndex}, rotation " +
                    $"{rotationIndex}, port {portIndex} has an unexpected " +
                    "attachment collision seam range.");
                coveredDirections.Add(primaryRelative.InboundNode.Direction);
                coveredDirections.Add(primaryRelative.OutboundNode.Direction);
                var opposite = proto.GetHighwayPort(
                    portIndex,
                    oppositeTransform);
                var translatedPrimary = new TileTransform(
                    new Tile3i(
                        opposite.Center.X - primaryRelative.Center.X,
                        opposite.Center.Y - primaryRelative.Center.Y,
                        opposite.Center.Z - primaryRelative.Center.Z),
                    rotation,
                    isReflected: false);
                var primary = proto.GetHighwayPort(
                    portIndex,
                    translatedPrimary);
                Require(primary.IsExactMateOf(opposite),
                    $"{kindName} base {baseDirectionIndex}, rotation " +
                    $"{rotationIndex}, port {portIndex} failed exact " +
                    "position/direction/lane-type mating.");
                RequireJunctionPairIsRejected(
                    dataType.Assembly,
                    proto,
                    translatedPrimary,
                    oppositeTransform,
                    kindName,
                    baseDirectionIndex,
                    rotationIndex,
                    portIndex);
            }
        }
    }

    private static void RequireJunctionPairIsRejected(
        Assembly assembly,
        HighwayJunctionProto proto,
        TileTransform first,
        TileTransform second,
        string kindName,
        int baseDirectionIndex,
        int rotationIndex,
        int portIndex)
    {
        var validatorType = assembly.GetType(
            "GroundRoads.HighwayJunctionPlacementValidator",
            throwOnError: true);
        var getCenter = validatorType.GetMethod(
            "GetCenter",
            BindingFlags.Static | BindingFlags.NonPublic);
        var areCentersTooClose = validatorType.GetMethod(
            "AreCentersTooClose",
            BindingFlags.Static | BindingFlags.NonPublic);
        var firstCenter = getCenter.Invoke(
            null,
            new object[] { proto, first });
        var secondCenter = getCenter.Invoke(
            null,
            new object[] { proto, second });

        Require((bool)areCentersTooClose.Invoke(
                null,
                new[] { firstCenter, secondCenter }),
            $"{kindName} base {baseDirectionIndex}, rotation " +
            $"{rotationIndex}, port {portIndex}: directly mated junctions " +
            "must be rejected by the spacing validator.");
    }

    private static void CheckTerrainAccessScope(
        Assembly assembly,
        HighwayJunctionProto proto,
        string kindName,
        int baseDirectionIndex)
    {
        var directorType = assembly.GetType(
            "GroundRoads.HighwayTrafficDirector",
            throwOnError: true);
        var addTerrainAccessNodes = directorType.GetMethod(
            "AddTerrainAccessNodes",
            BindingFlags.Static | BindingFlags.NonPublic);
        var entryNodes = new HashSet<RoadGraphNodeKey>();
        var exitNodes = new HashSet<RoadGraphNodeKey>();
        var transform = new TileTransform(
            Tile3i.Zero,
            Rotation90.Deg0,
            isReflected: false);

        addTerrainAccessNodes.Invoke(
            null,
            new object[] { proto, transform, entryNodes, exitNodes });
        Require(entryNodes.Count == proto.HighwayPortCount,
            $"{kindName} base {baseDirectionIndex}: terrain entry nodes " +
            "must contain only physical junction ports.");
        Require(exitNodes.Count == proto.HighwayPortCount,
            $"{kindName} base {baseDirectionIndex}: terrain exit nodes " +
            "must contain only physical junction ports.");

        for (var index = 0; index < proto.HighwayPortCount; index++)
        {
            var port = proto.GetHighwayPort(index, transform);
            Require(entryNodes.Contains(port.InboundNode),
                $"{kindName} base {baseDirectionIndex}: inbound port " +
                $"{index} is missing from terrain entries.");
            Require(exitNodes.Contains(port.OutboundNode),
                $"{kindName} base {baseDirectionIndex}: outbound port " +
                $"{index} is missing from terrain exits.");
        }
    }

    private static ImmutableArray<T> ToImmutable<T>(object values)
    {
        var list = ((IEnumerable)values).Cast<T>().ToList();
        var result = new ImmutableArrayBuilder<T>(list.Count);
        for (var index = 0; index < list.Count; index++)
        {
            result[index] = list[index];
        }

        return result.GetImmutableArrayAndClear();
    }

    private static void CheckRoundaboutTopology(ICollection laneData)
    {
        var lanes = laneData.Cast<object>().ToArray();
        for (var index = 0; index < 4; index++)
        {
            RequireSameNode(
                lanes[index],
                useEndOfLeft: true,
                lanes[4 + index],
                useStartOfRight: true,
                $"entry {index} -> ring {index}");
            RequireSameNode(
                lanes[4 + index],
                useEndOfLeft: true,
                lanes[4 + ((index + 1) & 3)],
                useStartOfRight: true,
                $"ring {index} -> ring {(index + 1) & 3}");
            RequireSameNode(
                lanes[8 + index],
                useEndOfLeft: false,
                lanes[4 + ((index + 3) & 3)],
                useStartOfRight: true,
                $"ring {(index + 3) & 3} -> exit {index}");
        }
    }

    private static void CheckVisualCoverage(
        ICollection laneTrajectories,
        ICollection visualTrajectories,
        string kindName,
        int baseDirectionIndex,
        double maximumDistance)
    {
        var visuals = visualTrajectories
            .Cast<RoadLaneTrajectory>()
            .ToArray();
        foreach (var lane in laneTrajectories.Cast<RoadLaneTrajectory>())
        {
            foreach (var lanePosition in lane.LaneCenterSamples)
            {
                var closestDistance = double.MaxValue;
                foreach (var visual in visuals)
                {
                    foreach (var visualPosition in visual.LaneCenterSamples)
                    {
                        closestDistance = Math.Min(
                            closestDistance,
                            lanePosition.Xy
                                .DistanceTo(visualPosition.Xy)
                                .ToDouble());
                    }
                }

                Require(closestDistance <= maximumDistance,
                    $"{kindName} base {baseDirectionIndex}: a lane centre " +
                    $"lies {closestDistance:0.###} tiles outside the " +
                    "visible asphalt union.");
            }
        }
    }

    private static void RequireSameNode(
        object leftLane,
        bool useEndOfLeft,
        object rightLane,
        bool useStartOfRight,
        string description)
    {
        var leftPrefix = useEndOfLeft ? "End" : "Start";
        var rightPrefix = useStartOfRight ? "Start" : "End";
        foreach (var suffix in new[] { "Position", "Direction", "Type" })
        {
            var left = leftLane.GetType()
                .GetField(leftPrefix + suffix)
                .GetValue(leftLane);
            var right = rightLane.GetType()
                .GetField(rightPrefix + suffix)
                .GetValue(rightLane);
            Require(Equals(left, right),
                $"Roundabout graph mismatch at {description}: {suffix}.");
        }
    }

    private static void CheckCollisionLayouts(Assembly assembly)
    {
        var networkData = assembly.GetType(
            "GroundRoads.HighwayNetworkData",
            throwOnError: true);
        var createJunctionLayout = networkData.GetMethod(
            "CreateJunctionLayout",
            BindingFlags.Static | BindingFlags.NonPublic);
        var junctionLayout = (EntityLayout)createJunctionLayout.Invoke(
            null,
            Array.Empty<object>());
        Require(junctionLayout.LayoutTiles.Length == 36,
            "Junction collision layout must contain a 6x6 occupied core.");
        Require(junctionLayout.CoreMin == new RelTile2i(-3, -3) &&
                junctionLayout.CoreMax == new RelTile2i(2, 2) &&
                junctionLayout.LayoutSize == new RelTile3i(6, 6, 1),
            "Junction collision layout must remain centred under rotation.");

        var roadsData = assembly.GetType(
            "GroundRoads.GroundRoadsData",
            throwOnError: true);
        var createMinimalLayout = roadsData.GetMethod(
            "CreateMinimalHighwayLayout",
            BindingFlags.Static | BindingFlags.NonPublic);
        var empty = EntityLayout.CreateEmpty(
            new RelTile2i(-5, -5),
            new RelTile2i(5, 5));
        var minimalLayout = (EntityLayout)createMinimalLayout.Invoke(
            null,
            new object[] { empty });
        Require(minimalLayout.LayoutTiles.Length == 1,
            "Highway compatibility layout must contain exactly one tile.");
        Require(minimalLayout.CoreMin == empty.CoreMin &&
                minimalLayout.CoreMax == empty.CoreMax &&
                minimalLayout.LayoutSize == empty.LayoutSize,
            "Minimal highway layout must preserve the source bounds.");
    }

    private static void CheckLegacyRampCompatibility(Assembly assembly)
    {
        Require(assembly.GetType(
                    "GroundRoads.HighwayRampPlacementControllerBase",
                    throwOnError: false) == null &&
                assembly.GetType(
                    "GroundRoads.HighwayOnRampPlacementController",
                    throwOnError: false) == null &&
                assembly.GetType(
                    "GroundRoads.HighwayOffRampPlacementController",
                    throwOnError: false) == null,
            "Ramp placement controllers must not be part of the active mod.");

        var networkProto = assembly.GetType(
            "GroundRoads.IHighwayNetworkProto",
            throwOnError: true);
        foreach (var typeName in new[]
                 {
                     "GroundRoads.HighwayOnRampProto",
                     "GroundRoads.HighwayOffRampProto"
                 })
        {
            var compatibilityType = assembly.GetType(
                typeName,
                throwOnError: true);
            Require(networkProto.IsAssignableFrom(compatibilityType),
                $"{typeName} must retain its compatibility marker.");
            var instance = FormatterServices.GetUninitializedObject(
                compatibilityType);
            var participates = (bool)networkProto
                .GetProperty("ParticipatesInHighwayNetwork")
                .GetValue(instance);
            Require(!participates,
                $"{typeName} must remain excluded from new highway routes.");
        }
    }

    private static void CheckLaneProjectionScope(Assembly assembly)
    {
        var effectsType = assembly.GetType(
            "GroundRoads.GroundRoadVehicleEffects",
            throwOnError: true);
        var requiresProjection = effectsType.GetMethod(
            "RequiresLaneProjection",
            BindingFlags.Static | BindingFlags.NonPublic);
        var segment = FormatterServices.GetUninitializedObject(
            assembly.GetType(
                "GroundRoads.HighwaySegmentProto",
                throwOnError: true));
        var junction = FormatterServices.GetUninitializedObject(
            assembly.GetType(
                "GroundRoads.HighwayJunctionProto",
                throwOnError: true));

        Require((bool)requiresProjection.Invoke(
                null,
                new[] { segment }),
            "Train-planned highway segments must retain lane projection.");
        Require(!(bool)requiresProjection.Invoke(
                null,
                new[] { junction }),
            "Junctions must retain native steering at entity transitions.");
    }

    private static void CheckJunctionSnapPolicy(Assembly assembly)
    {
        var discoveryType = assembly.GetType(
            "GroundRoads.HighwayPortDiscovery",
            throwOnError: true);
        var dragControllerType = assembly.GetType(
            "GroundRoads.GroundRoadDragController",
            throwOnError: true);
        var isEligiblePortSource = discoveryType.GetMethod(
            "IsEligiblePortSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        var isWithinSearchRange = GetPrivateStaticMethod(
            dragControllerType,
            "IsPortWithinSnapSearchRange",
            typeof(HighwayPort),
            typeof(Tile3i));
        var trySelect = GetPrivateStaticMethod(
            dragControllerType,
            "TrySelectClosestOpenPort",
            typeof(IEnumerable<HighwayPort>),
            typeof(Tile3i),
            typeof(bool),
            typeof(Tile3f),
            typeof(HighwayPort).MakeByRefType());
        var segment = FormatterServices.GetUninitializedObject(
            assembly.GetType(
                "GroundRoads.HighwaySegmentProto",
                throwOnError: true));
        var junction = FormatterServices.GetUninitializedObject(
            assembly.GetType(
                "GroundRoads.HighwayJunctionProto",
                throwOnError: true));

        Require((bool)isEligiblePortSource.Invoke(
                null,
                new object[] { segment, false }),
            "Junction tools must still snap to highway segment ends.");
        Require((bool)isEligiblePortSource.Invoke(
                null,
                new object[] { junction, true }),
            "The highway tool must still snap roads to junction arms.");
        Require(!(bool)isEligiblePortSource.Invoke(
                null,
                new object[] { junction, false }),
            "Junction tools must reject direct junction-to-junction snaps.");

        RoadGraphNodeKey node(double angleDegrees) =>
            new(
                RelTile1f.Zero,
                RelTile1f.Zero,
                0,
                CreateTrackDirection(
                    angleDegrees,
                    TrainTrackGradeFactor.G0),
                default);
        HighwayPort segmentPort(Tile3i center, double outwardAngle) =>
            new(center, default, node(outwardAngle));
        Tile3i extensionCursor(
            HighwayPort port,
            double longitudinal,
            double lateral = 0.0,
            int zOffset = 0)
        {
            var outward = port.OutboundNode.Direction.Direction.Vector2f
                .Normalized;
            var outwardX = outward.X.ToDouble();
            var outwardY = outward.Y.ToDouble();
            return new Tile3i(
                port.Center.X + (int)Math.Round(
                    longitudinal * outwardX + lateral * outwardY),
                port.Center.Y + (int)Math.Round(
                    longitudinal * outwardY - lateral * outwardX),
                port.Center.Z + zOffset);
        }

        var forwardSegmentPort = segmentPort(Tile3i.Zero, 0.0);
        var rightJunctionPort = new HighwayPort(
            new Tile3i(8, 0, 0),
            default,
            default,
            Tile3i.Zero);
        var leftJunctionPort = new HighwayPort(
            new Tile3i(-8, 0, 0),
            default,
            default,
            Tile3i.Zero);
        bool withinSearch(HighwayPort port, Tile3i cursor) =>
            (bool)isWithinSearchRange.Invoke(
                null,
                new object[] { port, cursor });
        Require(withinSearch(
                    forwardSegmentPort,
                    extensionCursor(
                        forwardSegmentPort,
                        20.0,
                        zOffset: 12)),
            "The spatial prefilter must retain an ordinary highway end " +
            "clicked 20 tiles ahead, regardless of terrain height.");
        Require(!withinSearch(
                    forwardSegmentPort,
                    extensionCursor(forwardSegmentPort, 25.0)) &&
                !withinSearch(
                    forwardSegmentPort,
                    extensionCursor(forwardSegmentPort, -10.0)) &&
                !withinSearch(
                    forwardSegmentPort,
                    extensionCursor(forwardSegmentPort, 10.0, 4.0)),
            "The continuation prefilter must reject overlong, wrong-side, " +
            "and laterally distant highway clicks.");
        Require(withinSearch(rightJunctionPort, Tile3i.Zero),
            "A road cursor over a junction body must discover its free " +
            "arms even when their physical ports are eight tiles away.");
        HighwayPort select(
            IEnumerable<HighwayPort> ports,
            Tile3i cursor,
            bool hasStart,
            Tile3f anchor)
        {
            var arguments = new object[]
            {
                ports,
                cursor,
                hasStart,
                anchor,
                default(HighwayPort)
            };
            Require((bool)trySelect.Invoke(null, arguments),
                "Expected an open highway port to be selected.");
            return (HighwayPort)arguments[4];
        }

        Require(select(
                    new[] { forwardSegmentPort },
                    extensionCursor(forwardSegmentPort, 10.0),
                    false,
                    Tile3f.Zero).Center == forwardSegmentPort.Center &&
                select(
                    new[] { forwardSegmentPort },
                    extensionCursor(
                        forwardSegmentPort,
                        20.0,
                        zOffset: 12),
                    false,
                    Tile3f.Zero).Center == forwardSegmentPort.Center,
            "A first click 10-20 tiles beyond an open highway end must " +
            "continue that highway, including onto higher dumped terrain.");
        foreach (var rejectedCursor in new[]
                 {
                     extensionCursor(forwardSegmentPort, 25.0),
                     extensionCursor(forwardSegmentPort, -10.0),
                     extensionCursor(forwardSegmentPort, 10.0, 4.0)
                 })
        {
            var rejectedArguments = new object[]
            {
                new[] { forwardSegmentPort },
                rejectedCursor,
                false,
                Tile3f.Zero,
                default(HighwayPort)
            };
            Require(!(bool)trySelect.Invoke(null, rejectedArguments),
                "An invalid highway-end continuation corridor was selected.");
        }
        var activeRoadArguments = new object[]
        {
            new[] { forwardSegmentPort },
            extensionCursor(forwardSegmentPort, 20.0),
            true,
            Tile3f.Zero,
            default(HighwayPort)
        };
        Require(!(bool)trySelect.Invoke(null, activeRoadArguments),
            "Long-range continuation snapping must apply only to the first " +
            "click, not to an active road goal.");

        var outward = forwardSegmentPort.OutboundNode.Direction.Direction
            .Vector2f.Normalized;
        var oppositeCenter = new Tile3i(
            -(int)Math.Round(outward.X.ToDouble()),
            -(int)Math.Round(outward.Y.ToDouble()),
            0);
        var oppositeSegmentPort = segmentPort(oppositeCenter, 180.0);
        Require(select(
                    new[] { oppositeSegmentPort, forwardSegmentPort },
                    extensionCursor(forwardSegmentPort, 20.0),
                    false,
                    Tile3f.Zero).Center == forwardSegmentPort.Center,
            "A one-tile highway must select only the exterior end facing " +
            "the user's distant continuation click.");

        var diagonalPort = segmentPort(new Tile3i(30, 30, 5), 45.0);
        Require(select(
                    new[] { diagonalPort },
                    extensionCursor(diagonalPort, 24.0, zOffset: 7),
                    false,
                    Tile3f.Zero).Center == diagonalPort.Center,
            "Diagonal highway ends must retain the same 24-tile, height-" +
            "independent continuation corridor.");
        var outsideDiagonalArguments = new object[]
        {
            new[] { diagonalPort },
            extensionCursor(diagonalPort, 25.0),
            false,
            Tile3f.Zero,
            default(HighwayPort)
        };
        Require(!(bool)trySelect.Invoke(null, outsideDiagonalArguments),
            "Diagonal cursor rounding must not extend continuation snapping " +
            "to the next lattice point beyond 24 tiles.");
        var outsideJunctionArguments = new object[]
        {
            new[] { rightJunctionPort },
            new Tile3i(0, 9, 0),
            false,
            Tile3f.Zero,
            default(HighwayPort)
        };
        Require(!(bool)trySelect.Invoke(null, outsideJunctionArguments),
            "The extended junction snap area must not leak beyond the " +
            "visible node neighborhood.");

        var selectedFacingArm = select(
            new[] { rightJunctionPort, leftJunctionPort },
            Tile3i.Zero,
            true,
            new Tile3f(-30, 0, 0));
        Require(selectedFacingArm.Center == leftJunctionPort.Center,
            "Clicking a junction body while drawing must choose the free " +
            "arm facing the active road anchor.");

        var directPort = new HighwayPort(
            new Tile3i(2, 0, 0),
            default,
            default);
        var selectedDirectPort = select(
            new[] { leftJunctionPort, directPort },
            Tile3i.Zero,
            true,
            new Tile3f(-30, 0, 0));
        Require(selectedDirectPort.Center == directPort.Center,
            "A direct physical-port hit must take priority over an extended " +
            "junction-body snap.");
    }

    private static void CheckHighwayReroutePolicy(Assembly assembly)
    {
        var pathFinderType = assembly.GetType(
            "GroundRoads.HighwayVehiclePathFinder",
            throwOnError: true);
        var trafficDirectorType = assembly.GetType(
            "GroundRoads.HighwayTrafficDirector",
            throwOnError: true);
        var isParticipatingHighwayProto = GetPrivateStaticMethod(
            pathFinderType,
            "IsParticipatingHighwayProto",
            typeof(IHighwayNetworkProto));
        var maxRouteCandidates = trafficDirectorType.GetField(
            "MaxRouteCandidates",
            BindingFlags.Static | BindingFlags.NonPublic);

        var segment = (IHighwayNetworkProto)
            FormatterServices.GetUninitializedObject(
                assembly.GetType(
                    "GroundRoads.HighwaySegmentProto",
                    throwOnError: true));
        var compatibilityRamp = (IHighwayNetworkProto)
            FormatterServices.GetUninitializedObject(
                assembly.GetType(
                    "GroundRoads.HighwayOnRampProto",
                    throwOnError: true));
        bool participates(IHighwayNetworkProto proto) =>
            (bool)isParticipatingHighwayProto.Invoke(
                null,
                new object[] { proto });

        Require(participates(segment),
            "Vehicles already driving on a participating highway must " +
            "keep their native current-road route during terrain retries.");
        Require(!participates(compatibilityRamp) && !participates(null),
            "Legacy compatibility ramps and missing roads must not trigger " +
            "the current-highway reroute shortcut.");
        Require((int)maxRouteCandidates.GetRawConstantValue() == 4,
            "Terrain-triggered highway routing must remain bounded to four " +
            "best candidates.");
    }

    private static void CheckJunctionSpacingPolicy(Assembly assembly)
    {
        var validatorType = assembly.GetType(
            "GroundRoads.HighwayJunctionPlacementValidator",
            throwOnError: true);
        var validatorInterface = typeof(IEntityAdditionValidator<>).MakeGenericType(
            typeof(LayoutEntityAddRequest));
        Require(validatorInterface.IsAssignableFrom(validatorType),
            "Junction spacing must participate in native preview and " +
            "command validation.");

        var minimumDistance = (int)validatorType.GetField(
                "MinimumCenterDistanceTiles",
                BindingFlags.Static | BindingFlags.NonPublic)
            .GetRawConstantValue();
        Require(minimumDistance == 24,
            "Junction spacing must reserve a 24-tile center distance.");

        var areCentersTooClose = validatorType.GetMethod(
            "AreCentersTooClose",
            BindingFlags.Static | BindingFlags.NonPublic);
        bool rejects(int x, int y) => (bool)areCentersTooClose.Invoke(
            null,
            new object[] { Tile2i.Zero, new Tile2i(x, y) });

        Require(rejects(0, 0),
            "Overlapping junction centers must be rejected.");
        Require(rejects(16, 0),
            "Direct cardinal junction neighbors must be rejected.");
        Require(rejects(23, 0),
            "Distances below the configured buffer must be rejected.");
        Require(!rejects(24, 0),
            "The exact 24-tile center distance must be allowed.");
        Require(!rejects(17, 17),
            "Rotationally equivalent distances outside the buffer must be " +
            "allowed.");
    }

    private static void CheckElevationRampSupport(Assembly assembly)
    {
        var roadsData = assembly.GetType(
            "GroundRoads.GroundRoadsData",
            throwOnError: true);
        var dragController = assembly.GetType(
            "GroundRoads.GroundRoadDragController",
            throwOnError: true);
        var trafficDirector = assembly.GetType(
            "GroundRoads.HighwayTrafficDirector",
            throwOnError: true);

        var reverseHeightCurve = GetPrivateStaticMethod(
            roadsData,
            "ReverseHeightCurve",
            typeof(CubicBezierCurve2f));
        var getExactDirection = GetPrivateStaticMethod(
            roadsData,
            "GetExactDirection",
            typeof(TrainTrackNodeDirection));
        var createLaneTrajectories = GetPrivateStaticMethod(
            roadsData,
            "CreateHighwayLaneTrajectories",
            typeof(TrainTrackSegmentsRel),
            typeof(RoadLaneTrajectory).MakeByRefType(),
            typeof(RoadLaneTrajectory).MakeByRefType());
        var getDirectionIndex = GetPrivateStaticMethod(
            dragController,
            "GetDirectionIndex",
            typeof(TrainTrackNodeDirection));
        var createPathFinderFlags = GetPrivateStaticMethod(
            dragController,
            "CreateHighwayPathFinderFlags");
        var getPreferredRampHeight = GetPrivateStaticMethod(
            dragController,
            "GetPreferredRampHeight",
            typeof(Tile3f),
            typeof(Tile3f));
        var getAdjustedRelativeHeight = GetPrivateStaticMethod(
            dragController,
            "GetAdjustedRelativeHeight",
            typeof(ThicknessTilesI),
            typeof(bool));
        var tryCreateFlatDirection = GetPrivateStaticMethod(
            dragController,
            "TryCreateFlatDirection",
            typeof(TrainTrackNodeDirection),
            typeof(TrainTrackNodeDirection).MakeByRefType());
        var getFlatEndDirectionRetryIndex = GetPrivateStaticMethod(
            dragController,
            "GetFlatEndDirectionRetryIndex",
            typeof(int),
            typeof(int));
        var canRetryFlatEndDirection = GetPrivateStaticMethod(
            dragController,
            "CanRetryFlatEndDirection",
            typeof(bool),
            typeof(int));
        var canInitializeFlatEndDirectionRetries = GetPrivateStaticMethod(
            dragController,
            "CanInitializeFlatEndDirectionRetries",
            typeof(bool),
            typeof(int),
            typeof(bool));
        var isTerrainRoadHeightDifferenceWithinTolerance =
            GetPrivateStaticMethod(
            dragController,
            "IsTerrainRoadHeightDifferenceWithinTolerance",
            typeof(double),
            typeof(bool));
        var isWithinAttachmentCollisionSeam = GetPrivateStaticMethod(
            dragController,
            "IsWithinAttachmentCollisionSeam",
            typeof(Tile2i),
            typeof(Tile2i),
            typeof(int));
        var doRoadSurfaceLinesOverlap = GetPrivateStaticMethod(
            dragController,
            "DoRoadSurfaceLinesOverlap",
            typeof(Tile3f),
            typeof(Tile3f),
            typeof(Tile3f),
            typeof(Tile3f));
        var doesBilinearTerrainCellMatchRoadTriangle = GetPrivateStaticMethod(
            dragController,
            "DoesBilinearTerrainCellMatchRoadTriangle",
            typeof(Tile3f),
            typeof(Tile3f),
            typeof(Tile3f),
            typeof(Tile2i),
            typeof(HeightTilesF),
            typeof(HeightTilesF),
            typeof(HeightTilesF),
            typeof(HeightTilesF),
            typeof(bool));
        var createRoadSurfaceStripTriangles = GetPrivateStaticMethod(
            dragController,
            "CreateRoadSurfaceStripTriangles",
            typeof(Tile3f),
            typeof(Tile3f),
            typeof(RelTile3f),
            typeof(RelTile3f),
            typeof(bool));
        var canUseAttachmentException = GetPrivateStaticMethod(
            dragController,
            "CanUseAttachmentException",
            typeof(bool),
            typeof(bool),
            typeof(bool),
            typeof(bool));
        var isTerrainHeightReachable = GetPrivateStaticMethod(
            trafficDirector,
            "IsTerrainHeightReachable",
            typeof(HeightTilesF),
            typeof(HeightTilesF));
        var areTerrainAccessHeightsReachable = GetPrivateStaticMethod(
            trafficDirector,
            "AreTerrainAccessHeightsReachable",
            typeof(HeightTilesF),
            typeof(HeightTilesF),
            typeof(HeightTilesF));

        CheckHeightCurveReversal(reverseHeightCurve);
        CheckExactGradeDirections(getExactDirection);
        CheckDirectionIndexIgnoresGrade(getDirectionIndex);
        CheckElevationPathFinderFlags(createPathFinderFlags);
        CheckPreferredRampHeight(getPreferredRampHeight);
        CheckRelativeHeightControls(getAdjustedRelativeHeight);
        CheckFlatEndDirections(
            tryCreateFlatDirection,
            getFlatEndDirectionRetryIndex,
            canRetryFlatEndDirection,
            canInitializeFlatEndDirectionRetries);
        CheckRoadSurfaceTerrainClearance(
            isTerrainRoadHeightDifferenceWithinTolerance,
            getExactDirection,
            isWithinAttachmentCollisionSeam,
            doRoadSurfaceLinesOverlap,
            doesBilinearTerrainCellMatchRoadTriangle,
            createRoadSurfaceStripTriangles,
            canUseAttachmentException);
        CheckElevationLaneTrajectories(
            createLaneTrajectories,
            getExactDirection);
        CheckTerrainHeightTolerance(
            isTerrainHeightReachable,
            areTerrainAccessHeightsReachable);
        CheckInclinedTerrainAccessIsRejected(trafficDirector);
    }

    private static void CheckRelativeHeightControls(MethodInfo adjustHeight)
    {
        ThicknessTilesI adjust(int current, bool raise) =>
            (ThicknessTilesI)adjustHeight.Invoke(
                null,
                new object[] { new ThicknessTilesI(current), raise });

        var maximum = TrainTrackPillarProto.MAX_PILLAR_HEIGHT.Value;
        Require(adjust(0, false) == ThicknessTilesI.Zero &&
                adjust(0, true) == ThicknessTilesI.One &&
                adjust(1, false) == ThicknessTilesI.Zero,
            "Q/E highway elevation must move one tile and never go below " +
            "terrain level.");
        Require(adjust(maximum - 1, true) ==
                TrainTrackPillarProto.MAX_PILLAR_HEIGHT &&
                adjust(maximum, true) ==
                TrainTrackPillarProto.MAX_PILLAR_HEIGHT,
            "E must clamp highway elevation to the native train-pillar " +
            "height limit.");
    }

    private static void CheckHighwaySupportModel(Assembly assembly)
    {
        var modelFactory = assembly.GetType(
            "GroundRoads.GroundRoadModelFactory",
            throwOnError: true);
        var shouldAppend = GetPrivateStaticMethod(
            modelFactory,
            "ShouldAppendSupportAtBlock",
            typeof(int),
            typeof(int));
        bool selected(int index, int count) =>
            (bool)shouldAppend.Invoke(null, new object[] { index, count });

        Require(selected(0, 1) &&
                !selected(-1, 1) &&
                !selected(1, 1),
            "A short planner piece must receive exactly one valid support.");
        Require(selected(0, 3) && selected(1, 3) && selected(2, 3) &&
                !selected(3, 3),
            "Every candidate already selected by the native train helper " +
            "must become a highway support.");

        var dragController = assembly.GetType(
            "GroundRoads.GroundRoadDragController",
            throwOnError: true);
        var getClearanceSamples = GetPrivateStaticMethod(
            dragController,
            "GetSupportClearanceLocalSamples",
            typeof(ImmutableArray<RoadLaneTrajectory>));
        var forward = new RelTile3f(1, 0, 0);
        var reverse = new RelTile3f(-1, 0, 0);
        var prefixes = ImmutableArray.Create(
            RelTile1f.Zero,
            1.0.Tiles());
        var lanes = ImmutableArray.Create(
            new RoadLaneTrajectory(
                ImmutableArray.Create(
                    new RelTile3f(0, 1, 0),
                    new RelTile3f(1, 1, 0)),
                ImmutableArray.Create(forward, forward),
                prefixes),
            new RoadLaneTrajectory(
                ImmutableArray.Create(
                    new RelTile3f(1, -1, 0),
                    new RelTile3f(0, -1, 0)),
                ImmutableArray.Create(reverse, reverse),
                prefixes));
        var clearanceSamples =
            ((IEnumerable<RelTile3f>)getClearanceSamples.Invoke(
                null,
                new object[] { lanes }))
            .ToArray();
        var sampledLateralCoordinates = clearanceSamples
            .Select(sample => Math.Round(sample.Y.ToDouble(), 6))
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        Require(clearanceSamples.Length == 12 &&
                sampledLateralCoordinates.SequenceEqual(
                    new[] { -2.0, -1.0, 0.0, 1.0, 2.0 }),
            "Supported-piece classification must sample both complete " +
            "two-tile lanes across the full four-tile highway width.");
    }

    private static void CheckHeightCurveReversal(MethodInfo reverse)
    {
        var source = new CubicBezierCurve2f(
            ImmutableArray.Create(
                new Vector2f(2, -4),
                new Vector2f(3, -1),
                new Vector2f(6, 3),
                new Vector2f(10, 7)));
        var reversed = (CubicBezierCurve2f)reverse.Invoke(
            null,
            new object[] { source });
        var restored = (CubicBezierCurve2f)reverse.Invoke(
            null,
            new object[] { reversed });

        RequireStrictlyIncreasingCurveX(
            source,
            "Source height curve");
        RequireStrictlyIncreasingCurveX(
            reversed,
            "Reversed height curve");
        Require(reversed.ControlPoints.First.Y ==
                source.ControlPoints.Last.Y &&
                reversed.ControlPoints.Last.Y ==
                source.ControlPoints.First.Y,
            "Reversing a height curve must exchange its endpoint heights.");

        Require(restored.ControlPoints.Length ==
                source.ControlPoints.Length,
            "Reversing a height curve twice changed its control-point count.");
        for (var index = 0;
             index < source.ControlPoints.Length;
             index++)
        {
            Require(restored.ControlPoints[index] ==
                    source.ControlPoints[index],
                $"Height-curve reversal is not involutive at control point " +
                $"{index}.");
        }
    }

    private static void RequireStrictlyIncreasingCurveX(
        CubicBezierCurve2f curve,
        string description)
    {
        for (var index = 1;
             index < curve.ControlPoints.Length;
             index++)
        {
            Require(curve.ControlPoints[index - 1].X <
                    curve.ControlPoints[index].X,
                $"{description} must remain strictly increasing in X.");
        }
    }

    private static void CheckExactGradeDirections(MethodInfo getExactDirection)
    {
        var ascending = CreateTrackDirection(
            90.0,
            TrainTrackGradeFactor.G8);
        var descending = CreateTrackDirection(
            90.0,
            TrainTrackGradeFactor.GMinus8);
        var ascendingVector = (RelTile3f)getExactDirection.Invoke(
            null,
            new object[] { ascending });
        var descendingVector = (RelTile3f)getExactDirection.Invoke(
            null,
            new object[] { descending });

        Require(ascendingVector.Z.IsPositive,
            "G8 exact directions must point upward in Z.");
        Require(descendingVector.Z.IsNegative,
            "GMinus8 exact directions must point downward in Z.");
        Require(ascendingVector.IsNormalized &&
                descendingVector.IsNormalized,
            "Exact graded directions must be normalized in three dimensions.");
        Require(ascendingVector.Xy == descendingVector.Xy &&
                ascendingVector.Z == -descendingVector.Z,
            "G8 and GMinus8 must differ only in their Z sign.");
    }

    private static void CheckDirectionIndexIgnoresGrade(MethodInfo getIndex)
    {
        var flat = CreateTrackDirection(
            90.0,
            TrainTrackGradeFactor.G0);
        var ascending = CreateTrackDirection(
            90.0,
            TrainTrackGradeFactor.G8);
        var descending = CreateTrackDirection(
            90.0,
            TrainTrackGradeFactor.GMinus8);
        var flatIndex = (int)getIndex.Invoke(null, new object[] { flat });
        var ascendingIndex = (int)getIndex.Invoke(
            null,
            new object[] { ascending });
        var descendingIndex = (int)getIndex.Invoke(
            null,
            new object[] { descending });

        Require(flatIndex == 4,
            "A 90-degree heading must map to direction index 4.");
        Require(ascendingIndex == flatIndex &&
                descendingIndex == flatIndex,
            "Direction-index selection must ignore the track grade.");
    }

    private static void CheckElevationPathFinderFlags(MethodInfo createFlags)
    {
        var flags = (TrainTrackPathFinderFlags)createFlags.Invoke(
            null,
            Array.Empty<object>());

        Require((flags & TrainTrackPathFinderFlags.GoalMustBeFlat) != 0,
            "Terrain ramps must still end on a flat planner seam.");
        Require((flags & TrainTrackPathFinderFlags.IgnoreCollisions) != 0,
            "The native 0.25-metre train-terrain prefilter must not reject " +
            "dumped-ground ramps before the full-width highway validator.");
        Require((flags & TrainTrackPathFinderFlags.DisallowG4) == 0,
            "G4 terrain ramps must be available to the planner.");
        Require((flags & TrainTrackPathFinderFlags.DisallowG8) == 0,
            "G8 terrain ramps must be available to the planner.");
    }

    private static void CheckPreferredRampHeight(MethodInfo getPreferredHeight)
    {
        object preferred(Tile3f start, Tile3f goal) =>
            getPreferredHeight.Invoke(null, new object[] { start, goal });

        Require(preferred(
                    new Tile3f(0, 0, 4),
                    new Tile3f(20, 0, 4)) == null,
            "A level highway must not receive an artificial height bias.");
        var ascending = (HeightTilesI)preferred(
            new Tile3f(0, 0, 1),
            new Tile3f(20, 0, 7));
        var descending = (HeightTilesI)preferred(
            new Tile3f(0, 0, 7),
            new Tile3f(20, 0, 1));
        Require(ascending.Value == 7 && descending.Value == 7,
            "Ascending and descending routes must prefer their higher end " +
            "so the planner keeps ramps above dumped terrain.");
    }

    private static void CheckFlatEndDirections(
        MethodInfo tryCreate,
        MethodInfo getRetryIndex,
        MethodInfo canRetry,
        MethodInfo canInitialize)
    {
        var source = CreateTrackDirection(
            112.5,
            TrainTrackGradeFactor.GMinus4);
        var arguments = new object[] { source, default(TrainTrackNodeDirection) };
        Require((bool)tryCreate.Invoke(null, arguments),
            "A graded endpoint direction must be convertible to G0.");
        var flat = (TrainTrackNodeDirection)arguments[1];
        Require(flat.Direction == source.Direction &&
                flat.GradeFactor == TrainTrackGradeFactor.G0,
            "Flat endpoint fallback must preserve XY heading and force G0.");

        var covered = new HashSet<int>();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            covered.Add((int)getRetryIndex.Invoke(
                null,
                new object[] { 5, attempt }));
        }

        Require(covered.Count == 16 && covered.All(x => x >= 0 && x < 16),
            "Flat endpoint retries must cover all 16 planner headings.");
        Require((int)getRetryIndex.Invoke(null, new object[] { 5, 0 }) == 5 &&
                (int)getRetryIndex.Invoke(null, new object[] { 5, 1 }) == 6 &&
                (int)getRetryIndex.Invoke(null, new object[] { 5, 2 }) == 4,
            "Flat endpoint retries must start with the closest headings.");

        bool retry(bool hasExplicitDirection, int attempts) =>
            (bool)canRetry.Invoke(
                null,
                new object[] { hasExplicitDirection, attempts });
        Require(retry(false, 1) && retry(false, 15),
            "Blocked free endpoints must try their remaining flat " +
            "directions.");
        Require(!retry(false, 0) && !retry(false, 16) && retry(true, 1) == false,
            "Flat endpoint retries must not run before initialization, " +
            "after exhaustion, or for an explicit snapped direction.");

        bool initialize(
            bool hasExplicitDirection,
            int attempts,
            bool hasGoalDelta) =>
            (bool)canInitialize.Invoke(
                null,
                new object[]
                {
                    hasExplicitDirection,
                    attempts,
                    hasGoalDelta
                });
        Require(initialize(false, 0, true),
            "An unresolved unrestricted search must initialize the flat " +
            "direction cycle from its direct goal heading.");
        Require(!initialize(true, 0, true) &&
                !initialize(false, 1, true) &&
                !initialize(false, 0, false),
            "Flat direction initialization must reject explicit goals, " +
            "active cycles, and zero-length goals.");
    }

    private static void CheckRoadSurfaceTerrainClearance(
        MethodInfo isDifferenceWithinTolerance,
        MethodInfo getExactDirection,
        MethodInfo isWithinAttachmentSeam,
        MethodInfo doSurfaceLinesOverlap,
        MethodInfo doesBilinearTerrainCellMatchRoadTriangle,
        MethodInfo createRoadSurfaceStripTriangles,
        MethodInfo canUseAttachmentException)
    {
        const double supportTolerance = 1.0;
        const double penetrationTolerance = 0.02;
        var maximumSupportDepth =
            TrainTrackPillarProto.MAX_PILLAR_HEIGHT.Value;
        var belowSupportRange = supportTolerance + 0.01;
        var belowMaximumSupportDepth = maximumSupportDepth + 0.01;
        var aboveVisibleDeck = penetrationTolerance + 0.01;
        bool differenceMatches(double difference, bool allowAirBelow) =>
            (bool)isDifferenceWithinTolerance.Invoke(
                null,
                new object[] { difference, allowAirBelow });

        Require(differenceMatches(0.0, false) &&
                differenceMatches(penetrationTolerance, false) &&
                differenceMatches(-supportTolerance, false),
            "Road surfaces must accept support below and only the small " +
            "visible-deck allowance above.");
        Require(!differenceMatches(aboveVisibleDeck, false) &&
                !differenceMatches(-belowSupportRange, false),
            "An unsupported road must reject both terrain burial and an " +
            "air gap beyond its support range.");
        Require(!differenceMatches(aboveVisibleDeck, true) &&
                differenceMatches(-belowSupportRange, true) &&
                differenceMatches(-maximumSupportDepth, true) &&
                !differenceMatches(-belowMaximumSupportDepth, true),
            "Supported G0/G4/G8 pieces may span lower dry terrain only " +
            "within the native six-tile pillar range, and the deck must " +
            "never disappear into terrain.");

        bool allowedAt(int x, int y, int seamRange =
            HighwayPort.DefaultAttachmentCollisionSeamRange) =>
            (bool)isWithinAttachmentSeam.Invoke(
                null,
                new object[]
                {
                    new Tile2i(x, y),
                    Tile2i.Zero,
                    seamRange
                });
        Require(allowedAt(2, 2) && allowedAt(-2, -2),
            "Exact attachment entities must be allowed across the full " +
            "four-tile seam width.");
        Require(!allowedAt(3, 0) && !allowedAt(0, -3),
            "Attachment exceptions must not leak beyond the endpoint seam.");
        Require(allowedAt(
                    4,
                    4,
                    HighwayPort.RoundaboutAttachmentCollisionSeamRange) &&
                !allowedAt(
                    5,
                    0,
                    HighwayPort.RoundaboutAttachmentCollisionSeamRange),
            "Roundabout attachment exceptions must cover their four-tile " +
            "entry flare without leaking farther into the node.");

        bool attachmentException(
            bool endpoint,
            bool open,
            bool selected,
            bool exactMate) =>
            (bool)canUseAttachmentException.Invoke(
                null,
                new object[] { endpoint, open, selected, exactMate });
        Require(attachmentException(true, true, true, true),
            "A selected open exact-mate endpoint must retain its seam " +
            "exception.");
        Require(!attachmentException(false, true, true, true) &&
                !attachmentException(true, false, true, true) &&
                !attachmentException(true, true, false, true) &&
                !attachmentException(true, true, true, false),
            "Closed, unselected, non-mating, or non-endpoint ports must " +
            "never receive a collision exception.");

        HeightTilesF height(double value) =>
            new(Fix32.FromDouble(value));
        Tile3f point(double x, double y, double z) =>
            new(
                Fix32.FromDouble(x),
                Fix32.FromDouble(y),
                Fix32.FromDouble(z));
        bool terrainTriangleMatches(
            Tile3f first,
            Tile3f second,
            Tile3f third,
            Tile2i cell,
            HeightTilesF bottomLeft,
            HeightTilesF bottomRight,
            HeightTilesF topLeft,
            HeightTilesF topRight,
            bool allowAirBelow = false) =>
            (bool)doesBilinearTerrainCellMatchRoadTriangle.Invoke(
                null,
                new object[]
                {
                    first,
                    second,
                    third,
                    cell,
                    bottomLeft,
                    bottomRight,
                    topLeft,
                    topRight,
                    allowAirBelow
                });
        bool bothWindingsMatch(
            Tile3f first,
            Tile3f second,
            Tile3f third,
            Tile2i cell,
            HeightTilesF bottomLeft,
            HeightTilesF bottomRight,
            HeightTilesF topLeft,
            HeightTilesF topRight,
            bool allowAirBelow = false) =>
            terrainTriangleMatches(
                first,
                second,
                third,
                cell,
                bottomLeft,
                bottomRight,
                topLeft,
                topRight,
                allowAirBelow) &&
            terrainTriangleMatches(
                first,
                third,
                second,
                cell,
                bottomLeft,
                bottomRight,
                topLeft,
                topRight,
                allowAirBelow);
        bool neitherWindingMatches(
            Tile3f first,
            Tile3f second,
            Tile3f third,
            Tile2i cell,
            HeightTilesF bottomLeft,
            HeightTilesF bottomRight,
            HeightTilesF topLeft,
            HeightTilesF topRight,
            bool allowAirBelow = false) =>
            !terrainTriangleMatches(
                first,
                second,
                third,
                cell,
                bottomLeft,
                bottomRight,
                topLeft,
                topRight,
                allowAirBelow) &&
            !terrainTriangleMatches(
                first,
                third,
                second,
                cell,
                bottomLeft,
                bottomRight,
                topLeft,
                topRight,
                allowAirBelow);

        var flatFirst = point(0.0, 0.0, 1.0);
        var flatSecond = point(2.0, 0.0, 1.0);
        var flatThird = point(0.0, 2.0, 1.0);
        Require(bothWindingsMatch(
                flatFirst,
                flatSecond,
                flatThird,
                Tile2i.Zero,
                HeightTilesF.One,
                HeightTilesF.One,
                HeightTilesF.One,
                HeightTilesF.One),
            "A flat road exactly supported by flat terrain must remain " +
            "valid in both triangle windings.");
        Require(bothWindingsMatch(
                flatFirst,
                flatSecond,
                flatThird,
                Tile2i.Zero,
                height(1.0 + penetrationTolerance),
                height(1.0 + penetrationTolerance),
                height(1.0 + penetrationTolerance),
                height(1.0 + penetrationTolerance)) &&
                bothWindingsMatch(
                    flatFirst,
                    flatSecond,
                    flatThird,
                    Tile2i.Zero,
                    height(1.0 - supportTolerance),
                    height(1.0 - supportTolerance),
                    height(1.0 - supportTolerance),
                    height(1.0 - supportTolerance)),
            "Triangle terrain support must use independent burial and air-" +
            "gap limits.");
        Require(neitherWindingMatches(
                flatFirst,
                flatSecond,
                flatThird,
                Tile2i.Zero,
                height(1.0 + aboveVisibleDeck),
                height(1.0 + aboveVisibleDeck),
                height(1.0 + aboveVisibleDeck),
                height(1.0 + aboveVisibleDeck)) &&
                neitherWindingMatches(
                    flatFirst,
                    flatSecond,
                    flatThird,
                    Tile2i.Zero,
                    height(1.0 - belowSupportRange),
                    height(1.0 - belowSupportRange),
                    height(1.0 - belowSupportRange),
                    height(1.0 - belowSupportRange)),
            "Triangle terrain support must reject burial and unsupported " +
            "gaps beyond their separate limits.");
        Require(neitherWindingMatches(
                flatFirst,
                flatSecond,
                flatThird,
                Tile2i.Zero,
                height(-0.01),
                height(-0.01),
                height(-0.01),
                height(-0.01)),
            "A flat road more than one tile above constant terrain must be " +
            "rejected without supports in both triangle windings.");
        Require(bothWindingsMatch(
                flatFirst,
                flatSecond,
                flatThird,
                Tile2i.Zero,
                height(-0.01),
                height(-0.01),
                height(-0.01),
                height(-0.01),
                true),
            "The same raised G0 surface must be accepted once highway " +
            "supports are enabled.");

        var g4First = point(0.0, 0.0, 0.0);
        var g4Second = point(4.0, 0.0, 1.0);
        var g4Third = point(0.0, 2.0, 0.0);
        Require(bothWindingsMatch(
                g4First,
                g4Second,
                g4Third,
                Tile2i.Zero,
                HeightTilesF.Zero,
                height(0.25),
                HeightTilesF.Zero,
                height(0.25)),
            "A terrain cell matching a G4 road plane must remain valid in " +
            "both triangle windings.");
        Require(bothWindingsMatch(
                point(0.0, 0.0, 1.01),
                point(4.0, 0.0, 2.01),
                point(0.0, 2.0, 1.01),
                Tile2i.Zero,
                HeightTilesF.Zero,
                height(0.25),
                HeightTilesF.Zero,
                height(0.25),
                true),
            "A G4 ramp must be allowed above lower dumped terrain in both " +
            "triangle windings.");
        Require(neitherWindingMatches(
                g4First,
                g4Second,
                g4Third,
                Tile2i.Zero,
                height(1.01),
                height(1.26),
                height(1.01),
                height(1.26),
                true),
            "Terrain penetrating a G4 ramp above the visible deck must be " +
            "rejected in both triangle windings.");

        var g8First = point(0.0, 0.0, 0.0);
        var g8Second = point(8.0, 0.0, 1.0);
        var g8Third = point(0.0, 2.0, 0.0);
        Require(bothWindingsMatch(
                g8First,
                g8Second,
                g8Third,
                Tile2i.Zero,
                HeightTilesF.Zero,
                height(0.125),
                HeightTilesF.Zero,
                height(0.125)),
            "A terrain cell matching a G8 road plane must remain valid in " +
            "both triangle windings.");
        Require(bothWindingsMatch(
                point(0.0, 0.0, 1.01),
                point(8.0, 0.0, 2.01),
                point(0.0, 2.0, 1.01),
                Tile2i.Zero,
                HeightTilesF.Zero,
                height(0.125),
                HeightTilesF.Zero,
                height(0.125),
                true),
            "A G8 ramp must be allowed above lower dumped terrain in both " +
            "triangle windings.");
        Require(neitherWindingMatches(
                g8First,
                g8Second,
                g8Third,
                Tile2i.Zero,
                height(1.01),
                height(1.135),
                height(1.01),
                height(1.135),
                true),
            "Terrain penetrating a G8 ramp above the visible deck must be " +
            "rejected in both triangle windings.");

        var unitFirst = point(0.0, 0.0, 0.0);
        var unitSecond = point(0.0, 1.0, 0.0);
        var unitThird = point(1.0, 0.0, 0.0);
        Require(bothWindingsMatch(
                unitFirst,
                unitSecond,
                unitThird,
                Tile2i.Zero,
                HeightTilesF.Zero,
                HeightTilesF.Zero,
                HeightTilesF.Zero,
                height(3.0 * penetrationTolerance)),
            "An h11 corner at three penetration tolerances must stay valid " +
            "because " +
            "the bilinear peak inside the unit triangle is only 3T/4.");
        Require(neitherWindingMatches(
                unitFirst,
                unitSecond,
                unitThird,
                Tile2i.Zero,
                HeightTilesF.Zero,
                HeightTilesF.Zero,
                HeightTilesF.Zero,
                height(5.0 * penetrationTolerance)),
            "An h11 corner at five penetration tolerances must be rejected " +
            "because " +
            "its off-corner bilinear peak inside the triangle exceeds T.");

        var baseHeight = 2.0;
        var lowerFirst = point(1.0, 0.0, baseHeight);
        var lowerSecond = point(0.0, 1.0, baseHeight);
        var lowerThird = point(
            1.0,
            1.0,
            baseHeight - supportTolerance / 2.0);
        var lowerTerrain = new[]
        {
            height(baseHeight - supportTolerance),
            height(baseHeight - supportTolerance +
                17.0 * supportTolerance / 32.0),
            height(baseHeight - supportTolerance +
                supportTolerance / 32.0),
            height(baseHeight - supportTolerance -
                7.0 * supportTolerance / 16.0)
        };
        Require(neitherWindingMatches(
                lowerFirst,
                lowerSecond,
                lowerThird,
                Tile2i.Zero,
                lowerTerrain[0],
                lowerTerrain[1],
                lowerTerrain[2],
                lowerTerrain[3]),
            "A convex bilinear minimum that creates an off-centre air gap " +
            "must be rejected in both triangle windings.");
        Require(bothWindingsMatch(
                lowerFirst,
                lowerSecond,
                lowerThird,
                Tile2i.Zero,
                lowerTerrain[0],
                lowerTerrain[1],
                lowerTerrain[2],
                lowerTerrain[3],
                true),
            "The same off-centre air gap must be permitted for a real " +
            "G4/G8 ramp strip.");

        var upperFirst = point(1.0, 0.0, baseHeight);
        var upperSecond = point(0.0, 1.0, baseHeight);
        var upperThird = point(
            1.0,
            1.0,
            baseHeight + penetrationTolerance / 2.0);
        var upperTerrain = new[]
        {
            height(baseHeight + penetrationTolerance),
            height(baseHeight + penetrationTolerance -
                17.0 * penetrationTolerance / 32.0),
            height(baseHeight + penetrationTolerance -
                penetrationTolerance / 32.0),
            height(baseHeight + penetrationTolerance +
                7.0 * penetrationTolerance / 16.0)
        };
        Require(neitherWindingMatches(
                upperFirst,
                upperSecond,
                upperThird,
                Tile2i.Zero,
                upperTerrain[0],
                upperTerrain[1],
                upperTerrain[2],
                upperTerrain[3]),
            "A concave bilinear maximum that clips an off-centre road " +
            "point must be rejected in both triangle windings.");
        Require(neitherWindingMatches(
                upperFirst,
                upperSecond,
                upperThird,
                Tile2i.Zero,
                upperTerrain[0],
                upperTerrain[1],
                upperTerrain[2],
                upperTerrain[3],
                true),
            "Ramp clearance must not hide an off-centre terrain " +
            "penetration.");

        var tangentTerrain = height(100.0);
        Require(bothWindingsMatch(
                unitFirst,
                unitSecond,
                unitThird,
                new Tile2i(1, 0),
                tangentTerrain,
                tangentTerrain,
                tangentTerrain,
                tangentTerrain),
            "A zero-area point tangency to a neighboring terrain cell " +
            "must not reject the road triangle.");

        var seamFirst = point(0.0, -1.0, 0.0);
        var seamSecond = point(4.0, -1.0, 1.0);
        var seamThird = point(4.0, 1.0, 1.0);
        var continuationFirst = point(4.0, -1.0, 1.0);
        var continuationSecond = point(8.0, -1.0, 2.0);
        var continuationThird = point(4.0, 1.0, 1.0);
        Require(bothWindingsMatch(
                seamFirst,
                seamSecond,
                seamThird,
                new Tile2i(4, 0),
                HeightTilesF.One,
                height(1.25),
                HeightTilesF.One,
                height(1.25)) &&
                bothWindingsMatch(
                    continuationFirst,
                    continuationSecond,
                    continuationThird,
                    new Tile2i(4, 0),
                    HeightTilesF.One,
                    height(1.25),
                    HeightTilesF.One,
                    height(1.25)),
            "An internal G4 seam must ignore the preceding triangle's " +
            "zero-area cell tangency while validating its continuation.");

        var inclinedDirection = (RelTile3f)getExactDirection.Invoke(
            null,
            new object[]
            {
                CreateTrackDirection(0.0, TrainTrackGradeFactor.G4)
            });
        var stripStart = point(2.0, 3.0, 4.0);
        var stripEnd = point(6.0, 3.0, 5.0);
        var strip = createRoadSurfaceStripTriangles.Invoke(
            null,
            new object[]
            {
                stripStart,
                stripEnd,
                inclinedDirection,
                inclinedDirection,
                false
            });
        (double X, double Y, double Z) stripVertex(
            object stripResult,
            int index)
        {
            var item = stripResult.GetType().GetField(
                $"Item{index}",
                BindingFlags.Instance | BindingFlags.Public);
            Require(item != null,
                $"Road surface strip tuple is missing Item{index}.");
            var vertex = item.GetValue(stripResult);
            var vertexType = vertex.GetType();
            return (
                (double)vertexType.GetField("X").GetValue(vertex),
                (double)vertexType.GetField("Y").GetValue(vertex),
                (double)vertexType.GetField("Z").GetValue(vertex));
        }

        bool sameVertex(
            (double X, double Y, double Z) first,
            (double X, double Y, double Z) second) =>
            Math.Abs(first.X - second.X) < 1e-9 &&
            Math.Abs(first.Y - second.Y) < 1e-9 &&
            Math.Abs(first.Z - second.Z) < 1e-9;
        var firstTriangleFirst = stripVertex(strip, 1);
        var firstTriangleSecond = stripVertex(strip, 2);
        var firstTriangleThird = stripVertex(strip, 3);
        var secondTriangleFirst = stripVertex(strip, 4);
        var secondTriangleSecond = stripVertex(strip, 5);
        var secondTriangleThird = stripVertex(strip, 6);
        var lateral = inclinedDirection.Normalized.Xy
            .RightOrthogonalVector;
        var expectedStartRight = (
            stripStart.X.ToDouble() + lateral.X.ToDouble(),
            stripStart.Y.ToDouble() + lateral.Y.ToDouble(),
            stripStart.Z.ToDouble());
        var expectedStartLeft = (
            stripStart.X.ToDouble() - lateral.X.ToDouble(),
            stripStart.Y.ToDouble() - lateral.Y.ToDouble(),
            stripStart.Z.ToDouble());
        var expectedEndLeft = (
            stripEnd.X.ToDouble() - lateral.X.ToDouble(),
            stripEnd.Y.ToDouble() - lateral.Y.ToDouble(),
            stripEnd.Z.ToDouble());
        var expectedEndRight = (
            stripEnd.X.ToDouble() + lateral.X.ToDouble(),
            stripEnd.Y.ToDouble() + lateral.Y.ToDouble(),
            stripEnd.Z.ToDouble());
        Require(sameVertex(firstTriangleFirst, expectedStartRight) &&
                sameVertex(firstTriangleSecond, expectedEndLeft) &&
                sameVertex(firstTriangleThird, expectedEndRight) &&
                sameVertex(secondTriangleFirst, expectedEndLeft) &&
                sameVertex(secondTriangleSecond, expectedStartRight) &&
                sameVertex(secondTriangleThird, expectedStartLeft),
            "Road strip triangles must mirror MeshBuilder's shared " +
            "startRight-to-endLeft diagonal and winding exactly.");
        var projectedWidth = Math.Sqrt(
            Math.Pow(firstTriangleFirst.X - secondTriangleThird.X, 2.0) +
            Math.Pow(firstTriangleFirst.Y - secondTriangleThird.Y, 2.0));
        var expectedProjectedWidth = 2.0 * Math.Sqrt(
            Math.Pow(lateral.X.ToDouble(), 2.0) +
            Math.Pow(lateral.Y.ToDouble(), 2.0));
        Require(Math.Abs(projectedWidth - expectedProjectedWidth) < 1e-9 &&
                projectedWidth < 2.0 - 1e-6,
            "Inclined road strips must retain MeshBuilder's projected XY " +
            "width instead of renormalizing the lateral vector.");

        var curvedEndDirection = (RelTile3f)getExactDirection.Invoke(
            null,
            new object[]
            {
                CreateTrackDirection(22.5, TrainTrackGradeFactor.G8)
            });
        var reflectedStrip = createRoadSurfaceStripTriangles.Invoke(
            null,
            new object[]
            {
                stripStart,
                stripEnd,
                inclinedDirection,
                curvedEndDirection,
                true
            });
        var reflectedStartLateral = inclinedDirection.Normalized.Xy
            .RightOrthogonalVector;
        var reflectedEndLateral = curvedEndDirection.Normalized.Xy
            .RightOrthogonalVector;
        var reflectedStartRight = (
            stripStart.X.ToDouble() -
                reflectedStartLateral.X.ToDouble(),
            stripStart.Y.ToDouble() -
                reflectedStartLateral.Y.ToDouble(),
            stripStart.Z.ToDouble());
        var reflectedStartLeft = (
            stripStart.X.ToDouble() +
                reflectedStartLateral.X.ToDouble(),
            stripStart.Y.ToDouble() +
                reflectedStartLateral.Y.ToDouble(),
            stripStart.Z.ToDouble());
        var reflectedEndLeft = (
            stripEnd.X.ToDouble() + reflectedEndLateral.X.ToDouble(),
            stripEnd.Y.ToDouble() + reflectedEndLateral.Y.ToDouble(),
            stripEnd.Z.ToDouble());
        var reflectedEndRight = (
            stripEnd.X.ToDouble() - reflectedEndLateral.X.ToDouble(),
            stripEnd.Y.ToDouble() - reflectedEndLateral.Y.ToDouble(),
            stripEnd.Z.ToDouble());
        Require(
            sameVertex(
                stripVertex(reflectedStrip, 1),
                reflectedStartRight) &&
            sameVertex(
                stripVertex(reflectedStrip, 2),
                reflectedEndLeft) &&
            sameVertex(
                stripVertex(reflectedStrip, 3),
                reflectedEndRight) &&
            sameVertex(
                stripVertex(reflectedStrip, 4),
                reflectedEndLeft) &&
            sameVertex(
                stripVertex(reflectedStrip, 5),
                reflectedStartRight) &&
            sameVertex(
                stripVertex(reflectedStrip, 6),
                reflectedStartLeft),
            "Reflected curved and inclined strips must preserve the local " +
            "MeshBuilder diagonal after handedness reversal.");

        bool overlaps(
            Tile3f firstStart,
            Tile3f firstEnd,
            Tile3f secondStart,
            Tile3f secondEnd) =>
            (bool)doSurfaceLinesOverlap.Invoke(
                null,
                new object[]
                {
                    firstStart,
                    firstEnd,
                    secondStart,
                    secondEnd
                });
        Require(overlaps(
                new Tile3f(-2, 0, 0),
                new Tile3f(2, 0, 0),
                new Tile3f(0, -2, 0),
                new Tile3f(0, 2, 0)),
            "Crossing same-height highway surfaces must collide even when " +
            "their minimal layouts do not.");
        Require(overlaps(
                new Tile3f(-2, 0, 0),
                new Tile3f(2, 0, 0),
                new Tile3f(0, -2, 1),
                new Tile3f(0, 2, 1)),
            "Terrain highways must reject XY crossings even at a different " +
            "height; this tool does not build overpasses.");
        Require(!overlaps(
                new Tile3f(-2, 0, 0),
                new Tile3f(2, 0, 0),
                new Tile3f(-2, 3, 0),
                new Tile3f(2, 3, 0)),
            "Separated parallel highway surfaces must remain buildable.");
    }

    private static void CheckElevationLaneTrajectories(
        MethodInfo createTrajectories,
        MethodInfo getExactDirection)
    {
        var gradeDirection = CreateTrackDirection(
            0.0,
            TrainTrackGradeFactor.G8);
        var exactDirection = (RelTile3f)getExactDirection.Invoke(
            null,
            new object[] { gradeDirection });
        var positions = ImmutableArray.Create(
            new RelTile3f(0, 0, 0),
            new RelTile3f(4, 0, Fix32.Half),
            new RelTile3f(8, 0, Fix32.One));
        var directions = ImmutableArray.Create(
            exactDirection,
            exactDirection,
            exactDirection);
        var firstLength =
            (positions[1] - positions[0]).Length.Tiles();
        var secondLength =
            (positions[2] - positions[1]).Length.Tiles();
        var source = new TrainTrackSegmentsRel(
            positions,
            directions,
            ImmutableArray.Create(
                RelTile1f.Zero,
                firstLength,
                firstLength + secondLength),
            gradeDirection,
            gradeDirection);
        var arguments = new object[] { source, null, null };

        createTrajectories.Invoke(null, arguments);
        var forward = (RoadLaneTrajectory)arguments[1];
        var reverse = (RoadLaneTrajectory)arguments[2];

        Require(forward.LaneCenterSamples.Length == positions.Length &&
                reverse.LaneCenterSamples.Length == positions.Length,
            "G8 lane conversion must preserve every 3D source sample.");
        for (var index = 0; index < positions.Length; index++)
        {
            var sourceIndex = positions.Length - index - 1;
            Require(forward.LaneCenterSamples[index].Z ==
                    positions[index].Z,
                $"Forward G8 lane lost Z at sample {index}.");
            Require(reverse.LaneCenterSamples[index].Z ==
                    positions[sourceIndex].Z,
                $"Reverse G8 lane lost Z at sample {index}.");
            Require(forward.LaneCenterSamples[index].Xy.DistanceTo(
                        positions[index].Xy) == Fix32.One,
                $"Forward lane offset is not exactly one tile at sample " +
                $"{index}.");
            Require(reverse.LaneCenterSamples[index].Xy.DistanceTo(
                        positions[sourceIndex].Xy) == Fix32.One,
                $"Reverse lane offset is not exactly one tile at sample " +
                $"{index}.");
            Require(reverse.LaneDirectionSamples[index] ==
                    -forward.LaneDirectionSamples[sourceIndex],
                $"Reverse lane direction is not opposite at sample {index}.");
            Require(forward.LaneDirectionSamples[index].IsNormalized &&
                    reverse.LaneDirectionSamples[index].IsNormalized,
                $"G8 lane direction is not normalized at sample {index}.");
        }

        CheckTrajectoryLengthPrefixes("Forward G8 lane", forward);
        CheckTrajectoryLengthPrefixes("Reverse G8 lane", reverse);
        Require(forward.SegmentLengthsPrefixSums.Last.IsNear(
                    reverse.SegmentLengthsPrefixSums.Last) &&
                forward.SegmentLengthsPrefixSums.Last.IsNear(
                    firstLength + secondLength),
            "Forward and reverse G8 lanes must retain the source 3D length.");
    }

    private static void CheckTrajectoryLengthPrefixes(
        string description,
        RoadLaneTrajectory trajectory)
    {
        Require(trajectory.SegmentLengthsPrefixSums.Length ==
                trajectory.LaneCenterSamples.Length,
            $"{description} has a mismatched length-prefix count.");
        Require(trajectory.SegmentLengthsPrefixSums.First == RelTile1f.Zero,
            $"{description} must start with a zero length prefix.");

        var expected = RelTile1f.Zero;
        for (var index = 1;
             index < trajectory.LaneCenterSamples.Length;
             index++)
        {
            expected += (trajectory.LaneCenterSamples[index] -
                         trajectory.LaneCenterSamples[index - 1])
                .Length
                .Tiles();
            Require(trajectory.SegmentLengthsPrefixSums[index].IsNear(
                        expected),
                $"{description} has an incorrect prefix at sample {index}.");
        }
    }

    private static void CheckTerrainHeightTolerance(
        MethodInfo isReachable,
        MethodInfo areAccessHeightsReachable)
    {
        var roadHeight = HeightTilesF.Zero;
        var exactTolerance = roadHeight + 0.5.TilesThick();
        var aboveTolerance = roadHeight + 0.51.TilesThick();
        bool reachable(HeightTilesF left, HeightTilesF right) =>
            (bool)isReachable.Invoke(null, new object[] { left, right });
        bool accessReachable(
            HeightTilesF road,
            HeightTilesF endpoint,
            HeightTilesF access) =>
            (bool)areAccessHeightsReachable.Invoke(
                null,
                new object[] { road, endpoint, access });

        Require(reachable(roadHeight, roadHeight),
            "Matching road and terrain heights must be reachable.");
        Require(reachable(roadHeight, exactTolerance) &&
                reachable(exactTolerance, roadHeight),
            "A half-tile terrain-height difference must be reachable in " +
            "both directions.");
        Require(!reachable(roadHeight, aboveTolerance) &&
                !reachable(aboveTolerance, roadHeight),
            "A terrain-height difference above half a tile must be rejected " +
            "in both directions.");
        Require(accessReachable(
                    roadHeight,
                    exactTolerance,
                    exactTolerance),
            "Matching endpoint and recovery-tile heights must be accepted.");
        Require(!accessReachable(
                    roadHeight,
                    aboveTolerance,
                    roadHeight),
            "A nearby matching recovery tile must not hide a vertical gap " +
            "below the road endpoint.");
    }

    private static void CheckInclinedTerrainAccessIsRejected(Type directorType)
    {
        var tryResolve = directorType.GetMethod(
            "TryResolveTerrainAccess",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(RoadGraphNodeKey),
                typeof(VehiclePathFindingParams),
                typeof(RoadPathSegment),
                typeof(bool),
                typeof(Tile2i).MakeByRefType()
            },
            modifiers: null);
        Require(tryResolve != null,
            "HighwayTrafficDirector.TryResolveTerrainAccess with the " +
            "expected private signature is missing.");
        var director = FormatterServices.GetUninitializedObject(directorType);

        foreach (var grade in new[]
                 {
                     TrainTrackGradeFactor.G8,
                     TrainTrackGradeFactor.GMinus8
                 })
        {
            var node = new RoadGraphNodeKey(
                RelTile1f.Zero,
                RelTile1f.Zero,
                0,
                CreateTrackDirection(0.0, grade),
                default);
            var arguments = new object[]
            {
                node,
                null,
                default(RoadPathSegment),
                false,
                Tile2i.Zero
            };
            Require(!(bool)tryResolve.Invoke(director, arguments),
                $"A {grade} road node must not become a terrain access.");
        }
    }

    private static TrainTrackNodeDirection CreateTrackDirection(
        double angleDegrees,
        TrainTrackGradeFactor grade)
    {
        Require(TrainTrackNodeDirection.TryCreateFromAngle(
                angleDegrees.Degrees(),
                grade,
                out var direction,
                out _),
            $"Could not create a {grade} direction at {angleDegrees} " +
            "degrees for elevation smoke checks.");
        return direction;
    }

    private static void CheckHighwayConstructionEconomy(
        Assembly assembly,
        Type kindType)
    {
        var roadsDataType = assembly.GetType(
            "GroundRoads.GroundRoadsData",
            throwOnError: true);
        var shouldUseConstructionMaterials = GetPrivateStaticMethod(
            roadsDataType,
            "ShouldUseConstructionMaterialsForHighwaySegment",
            typeof(bool));
        Require((bool)shouldUseConstructionMaterials.Invoke(
                    null,
                    new object[] { false }) &&
                !(bool)shouldUseConstructionMaterials.Invoke(
                    null,
                    new object[] { true }),
            "Flat highway pieces must retain material sites, while G4/G8 " +
            "pieces must complete immediately to avoid an unreachable ramp " +
            "construction deadlock.");

        var dragController = assembly.GetType(
            "GroundRoads.GroundRoadDragController",
            throwOnError: true);
        var shouldCompleteFlatPieceImmediately = GetPrivateStaticMethod(
            dragController,
            "ShouldCompleteFlatPieceImmediately",
            typeof(double));
        var shouldCompletePlanImmediately = GetPrivateStaticMethod(
            dragController,
            "ShouldCompletePlanImmediately",
            typeof(int));
        bool completesImmediately(double clearance) =>
            (bool)shouldCompleteFlatPieceImmediately.Invoke(
                null,
                new object[] { clearance });
        Require(!completesImmediately(0.24) &&
                completesImmediately(0.25) &&
                completesImmediately(1.0),
            "Ground-supported G0 pieces must retain material costs, while " +
            "visibly pillar-supported G0 pieces complete immediately so " +
            "their construction site cannot deadlock in the air.");
        Require(!(bool)shouldCompletePlanImmediately.Invoke(
                    null,
                    new object[] { 0 }) &&
                (bool)shouldCompletePlanImmediately.Invoke(
                    null,
                    new object[] { 1 }) &&
                (bool)shouldCompletePlanImmediately.Invoke(
                    null,
                    new object[] { 8 }),
            "A connected plan must switch to one immediate batch as soon " +
            "as any flat piece needs visible supports; a ground-only plan " +
            "must remain paid.");

        var idsType = assembly.GetType(
            "GroundRoads.GroundRoadMaterialIds",
            throwOnError: true);
        var asphaltId = idsType.GetField(
                "Asphalt",
                BindingFlags.Static | BindingFlags.Public)
            .GetValue(null);
        var recipeId = idsType.GetField(
                "AsphaltMixing",
                BindingFlags.Static | BindingFlags.Public)
            .GetValue(null);
        Require(asphaltId.ToString() == "Product_GroundRoads_Asphalt",
            "The asphalt product ID must remain stable for save games.");
        Require(recipeId.ToString() == "GroundRoads_AsphaltMixing",
            "The asphalt recipe ID must remain stable for save games.");

        var costsType = assembly.GetType(
            "GroundRoads.HighwayConstructionCosts",
            throwOnError: true);
        var forLength = costsType.GetMethod(
            "ForLength",
            BindingFlags.Static | BindingFlags.Public);
        var forNode = costsType.GetMethod(
            "ForNode",
            BindingFlags.Static | BindingFlags.Public);
        Require(forLength != null && forNode != null,
            "Highway material cost factories are missing.");

        void requireAmounts(object amounts, int gravel, int asphalt,
            string description)
        {
            var amountType = amounts.GetType();
            var actualGravel = (int)amountType.GetField("Gravel")
                .GetValue(amounts);
            var actualAsphalt = (int)amountType.GetField("Asphalt")
                .GetValue(amounts);
            Require(actualGravel == gravel && actualAsphalt == asphalt,
                $"{description}: expected {gravel} gravel/{asphalt} " +
                $"asphalt, got {actualGravel}/{actualAsphalt}.");
        }

        requireAmounts(forLength.Invoke(null, new object[] { 0.1 }), 2, 1,
            "Sub-tile planner piece");
        requireAmounts(forLength.Invoke(null, new object[] { 1.0 }), 2, 1,
            "One-tile planner piece");
        requireAmounts(forLength.Invoke(null, new object[] { 1.01 }), 4, 2,
            "Rounded two-tile planner piece");
        requireAmounts(forLength.Invoke(null, new object[] { 8.0 }), 16, 8,
            "Eight-tile highway segment");

        requireAmounts(
            forNode.Invoke(
                null,
                new[] { Enum.Parse(kindType, "TIntersection") }),
            24,
            12,
            "T intersection");
        requireAmounts(
            forNode.Invoke(
                null,
                new[] { Enum.Parse(kindType, "CrossIntersection") }),
            32,
            16,
            "Cross intersection");
        requireAmounts(
            forNode.Invoke(
                null,
                new[] { Enum.Parse(kindType, "Roundabout") }),
            48,
            24,
            "Roundabout");

        foreach (var invalidLength in new[]
                 {
                     0.0,
                     -1.0,
                     double.NaN,
                     double.PositiveInfinity
                 })
        {
            try
            {
                forLength.Invoke(null, new object[] { invalidLength });
                throw new InvalidOperationException(
                    $"Invalid road length {invalidLength} was accepted.");
            }
            catch (TargetInvocationException error)
                when (error.InnerException is ArgumentOutOfRangeException)
            {
            }
        }
    }

    private static MethodInfo GetPrivateStaticMethod(
        Type type,
        string name,
        params Type[] parameterTypes)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        Require(method != null,
            $"{type.FullName}.{name} with the expected private static " +
            "signature is missing.");
        return method;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
