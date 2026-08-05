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
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Trains;

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
            CheckJunctionSpacingPolicy(assembly);

            Console.WriteLine(
                "Geometry smoke checks passed: 4 variants each; " +
                "T=6, +=12, roundabout=12 shared lanes, 16 headings with " +
                "exact port mates, external-only terrain access, buffered " +
                "junction placement with a 24-tile exclusion zone, native " +
                "junction steering, no ramp tools.");
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
        var isEligiblePortSource = discoveryType.GetMethod(
            "IsEligiblePortSource",
            BindingFlags.Static | BindingFlags.NonPublic);
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
