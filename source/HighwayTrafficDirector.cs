using System;
using System.Collections.Generic;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Dynamic;
using Mafi.Core.PathFinding;
using Mafi.Core.Roads;
using Mafi.Core.Terrain;
using Mafi.Core.Trains;

namespace GroundRoads;

/// <summary>
/// Receives a vehicle's start and destination and selects a directed highway
/// path between exact highway lane graph nodes.
/// </summary>
public sealed class HighwayTrafficDirector : IDisposable
{
    private const int HighwaySpeedPercent = 140;
    private const int HighwayPreferencePercent = 110;
    // Terrain reachability is only known after the native path finder tests a
    // candidate. Keep enough alternatives for obstructed or
    // navigate-closeby destinations without allowing an unbounded search.
    private const int MaxRouteCandidates = 8;
    private const int MaxSafeRoadApproachTiles = 20;

    internal readonly struct Route
    {
        public readonly RoadGraphNodeKey EntryRoadNode;
        public readonly RoadGraphNodeKey ExitRoadNode;
        public readonly Tile2i EntryTile;
        public readonly Tile2i ExitTile;
        public readonly Fix32 EntryConnectorDistance;
        public readonly Fix32 ExitConnectorDistance;
        public readonly ImmutableArray<RoadPathSegment> RoadPath;
        public readonly Fix32 RoadDistance;
        public readonly Fix32 EstimatedCost;

        public Route(
            RoadGraphNodeKey entryRoadNode,
            RoadGraphNodeKey exitRoadNode,
            Tile2i entryTile,
            Tile2i exitTile,
            Fix32 entryConnectorDistance,
            Fix32 exitConnectorDistance,
            ImmutableArray<RoadPathSegment> roadPath,
            Fix32 roadDistance,
            Fix32 estimatedCost)
        {
            EntryRoadNode = entryRoadNode;
            ExitRoadNode = exitRoadNode;
            EntryTile = entryTile;
            ExitTile = exitTile;
            EntryConnectorDistance = entryConnectorDistance;
            ExitConnectorDistance = exitConnectorDistance;
            RoadPath = roadPath;
            RoadDistance = roadDistance;
            EstimatedCost = estimatedCost;
        }
    }

    private readonly struct HighwayEdge
    {
        public readonly RoadGraphNodeKey Start;
        public readonly RoadGraphNodeKey End;
        public readonly RoadPathSegment Segment;
        public readonly Fix32 Distance;

        public HighwayEdge(
            RoadGraphNodeKey start,
            RoadGraphNodeKey end,
            RoadPathSegment segment,
            Fix32 distance)
        {
            Start = start;
            End = end;
            Segment = segment;
            Distance = distance;
        }
    }

    private readonly struct PreviousStep
    {
        public readonly RoadGraphNodeKey Node;
        public readonly HighwayEdge Edge;

        public PreviousStep(RoadGraphNodeKey node, HighwayEdge edge)
        {
            Node = node;
            Edge = edge;
        }
    }

    private readonly struct QueueItem
    {
        public readonly RoadGraphNodeKey Node;
        public readonly Fix32 Cost;

        public QueueItem(RoadGraphNodeKey node, Fix32 cost)
        {
            Node = node;
            Cost = cost;
        }
    }

    private readonly struct CandidateExit
    {
        public readonly RoadGraphNodeKey Node;
        public readonly Tile2i TerrainTile;
        public readonly Fix32 ConnectorDistance;
        public readonly Fix32 Cost;

        public CandidateExit(
            RoadGraphNodeKey node,
            Tile2i terrainTile,
            Fix32 connectorDistance,
            Fix32 cost)
        {
            Node = node;
            TerrainTile = terrainTile;
            ConnectorDistance = connectorDistance;
            Cost = cost;
        }
    }

    private sealed class MinQueue
    {
        private readonly List<QueueItem> m_items = new();

        public bool IsEmpty => m_items.Count == 0;

        public void Push(QueueItem item)
        {
            m_items.Add(item);
            var index = m_items.Count - 1;
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (!(m_items[index].Cost < m_items[parent].Cost))
                {
                    break;
                }

                (m_items[parent], m_items[index]) =
                    (m_items[index], m_items[parent]);
                index = parent;
            }
        }

        public QueueItem Pop()
        {
            var result = m_items[0];
            var last = m_items[m_items.Count - 1];
            m_items.RemoveAt(m_items.Count - 1);
            if (m_items.Count == 0)
            {
                return result;
            }

            m_items[0] = last;
            var index = 0;
            while (true)
            {
                var left = index * 2 + 1;
                if (left >= m_items.Count)
                {
                    break;
                }

                var right = left + 1;
                var smallest = right < m_items.Count &&
                    m_items[right].Cost < m_items[left].Cost
                        ? right
                        : left;
                if (!(m_items[smallest].Cost < m_items[index].Cost))
                {
                    break;
                }

                (m_items[index], m_items[smallest]) =
                    (m_items[smallest], m_items[index]);
                index = smallest;
            }

            return result;
        }
    }

    private readonly IEntitiesManager m_entitiesManager;
    private readonly IRoadsManager m_roadsManager;
    private readonly ClearancePathabilityProvider m_pathabilityProvider;
    private readonly TerrainManager m_terrainManager;
    private Dictionary<RoadGraphNodeKey, List<HighwayEdge>> m_cachedGraph;
    private HashSet<RoadGraphNodeKey> m_entryAccessNodes = new();
    private HashSet<RoadGraphNodeKey> m_exitAccessNodes = new();
    private bool m_graphIsDirty = true;

    public HighwayTrafficDirector(
        IEntitiesManager entitiesManager,
        IRoadsManager roadsManager,
        ClearancePathabilityProvider pathabilityProvider,
        TerrainManager terrainManager)
    {
        m_entitiesManager = entitiesManager;
        m_roadsManager = roadsManager;
        m_pathabilityProvider = pathabilityProvider;
        m_terrainManager = terrainManager;
        m_roadsManager.RoadBecamePathable.AddNonSaveable(
            this,
            OnRoadChanged);
        m_roadsManager.RoadBecameUnpathable.AddNonSaveable(
            this,
            OnRoadChanged);
    }

    public void Dispose()
    {
        m_roadsManager.RoadBecamePathable.RemoveNonSaveable(
            this,
            OnRoadChanged);
        m_roadsManager.RoadBecameUnpathable.RemoveNonSaveable(
            this,
            OnRoadChanged);
        m_cachedGraph = null;
        m_entryAccessNodes.Clear();
        m_exitAccessNodes.Clear();
    }

    internal bool TryCreateRoutes(
        IPathFindingVehicle vehicle,
        IIndexable<Tile2i> starts,
        IIndexable<Tile2i> goals,
        VehiclePathFindingParams pathFindingParams,
        Fix32 directCost,
        out List<Route> routes)
    {
        routes = new List<Route>();
        if (directCost.IsNotPositive || starts.Count == 0 || goals.Count == 0 ||
            pathFindingParams.RoadLaneTypeMask == RoadLaneType.MaskAllowNone)
        {
            return false;
        }

        var graph = GetGraph();
        if (graph.Count == 0)
        {
            return false;
        }

        // A lane graph node is a precise road coordinate, not necessarily a
        // terrain-pathable tile for a truck-sized clearance. Resolve a small
        // local terrain connector once per directed node. This mirrors the
        // native start-tile recovery that otherwise only helps road exits.
        var terrainAccessTiles =
            new Dictionary<RoadGraphNodeKey, Tile2i>();
        var inaccessibleNodes = new HashSet<RoadGraphNodeKey>();

        bool tryGetTerrainAccess(
            RoadGraphNodeKey node,
            RoadPathSegment adjacentRoadSegment,
            out Tile2i accessTile)
        {
            if (terrainAccessTiles.TryGetValue(node, out accessTile))
            {
                return true;
            }

            if (inaccessibleNodes.Contains(node) ||
                !TryResolveTerrainAccess(
                    node,
                    pathFindingParams,
                    adjacentRoadSegment,
                    requireSafeFirstTarget: false,
                    out accessTile))
            {
                inaccessibleNodes.Add(node);
                return false;
            }

            terrainAccessTiles.Add(node, accessTile);
            return true;
        }

        var distances = new Dictionary<RoadGraphNodeKey, Fix32>();
        var roadDistances = new Dictionary<RoadGraphNodeKey, Fix32>();
        var previous = new Dictionary<RoadGraphNodeKey, PreviousStep>();
        var entryRoadNodes =
            new Dictionary<RoadGraphNodeKey, RoadGraphNodeKey>();
        var entryTiles = new Dictionary<RoadGraphNodeKey, Tile2i>();
        var entryConnectorDistances =
            new Dictionary<RoadGraphNodeKey, Fix32>();
        var queue = new MinQueue();

        foreach (var pair in graph)
        {
            if (!m_entryAccessNodes.Contains(pair.Key))
            {
                continue;
            }

            // Seed only complete, safe first road edges. A zero-road label at
            // the root could otherwise be cheaper yet unable to enter its
            // first segment, blocking a valid label arriving via highway.
            foreach (var edge in pair.Value)
            {
                if (!pathFindingParams.CanUseRoadType(edge.Start.LaneType) ||
                    !TryResolveTerrainAccess(
                        pair.Key,
                        pathFindingParams,
                        edge.Segment,
                        requireSafeFirstTarget: true,
                        out var entryTile))
                {
                    continue;
                }

                var entryConnectorDistance =
                    ComputeConnectorDistance(
                        entryTile,
                        pair.Key,
                        pathFindingParams);
                var terrainEntryCost =
                    MinimumDistance(starts, entryTile) +
                    entryConnectorDistance;
                var firstCost = terrainEntryCost +
                    ComputeHighwayTravelCost(edge.Distance);
                if (distances.TryGetValue(edge.End, out var oldCost) &&
                    !(firstCost < oldCost))
                {
                    continue;
                }

                distances[edge.End] = firstCost;
                roadDistances[edge.End] = edge.Distance;
                entryRoadNodes[edge.End] = pair.Key;
                entryTiles[edge.End] = entryTile;
                entryConnectorDistances[edge.End] =
                    entryConnectorDistance;
                previous[edge.End] = new PreviousStep(pair.Key, edge);
                queue.Push(new QueueItem(edge.End, firstCost));
            }
        }

        if (queue.IsEmpty)
        {
            return false;
        }

        while (!queue.IsEmpty)
        {
            var current = queue.Pop();
            if (!distances.TryGetValue(current.Node, out var currentCost) ||
                current.Cost != currentCost)
            {
                continue;
            }

            if (!graph.TryGetValue(current.Node, out var outgoing))
            {
                continue;
            }

            foreach (var edge in outgoing)
            {
                if (!pathFindingParams.CanUseRoadType(edge.Start.LaneType))
                {
                    continue;
                }

                var nextCost = currentCost +
                    ComputeHighwayTravelCost(edge.Distance);
                if (distances.TryGetValue(edge.End, out var knownCost) &&
                    !(nextCost < knownCost))
                {
                    continue;
                }

                distances[edge.End] = nextCost;
                roadDistances[edge.End] =
                    roadDistances[current.Node] + edge.Distance;
                entryRoadNodes[edge.End] =
                    entryRoadNodes[current.Node];
                entryTiles[edge.End] = entryTiles[current.Node];
                entryConnectorDistances[edge.End] =
                    entryConnectorDistances[current.Node];
                previous[edge.End] = new PreviousStep(current.Node, edge);
                queue.Push(new QueueItem(edge.End, nextCost));
            }
        }

        var exits = new List<CandidateExit>();
        foreach (var distance in distances)
        {
            if (!previous.TryGetValue(
                    distance.Key,
                    out var exitStep) ||
                !m_exitAccessNodes.Contains(distance.Key))
            {
                continue;
            }

            if (!tryGetTerrainAccess(
                    distance.Key,
                    exitStep.Edge.Segment,
                    out var exitTile))
            {
                continue;
            }

            var exitConnectorDistance =
                ComputeConnectorDistance(
                    exitTile,
                    distance.Key,
                    pathFindingParams);
            var totalCost = distance.Value +
                exitConnectorDistance +
                MinimumDistance(goals, exitTile);
            if (ShouldPreferHighway(directCost, totalCost))
            {
                exits.Add(new CandidateExit(
                    distance.Key,
                    exitTile,
                    exitConnectorDistance,
                    totalCost));
            }
        }

        exits.Sort((left, right) =>
        {
            if (left.Cost < right.Cost)
            {
                return -1;
            }

            return left.Cost > right.Cost ? 1 : 0;
        });

        foreach (var candidate in exits)
        {
            if (!entryRoadNodes.TryGetValue(
                    candidate.Node,
                    out var entryRoadNode))
            {
                continue;
            }

            var path = ReconstructPath(
                candidate.Node,
                entryRoadNode,
                previous);
            if (path.IsEmpty || !ContainsHighwaySegment(path) ||
                !IsContinuousAndPathable(path) ||
                !entryTiles.TryGetValue(candidate.Node, out var entryTile) ||
                !entryConnectorDistances.TryGetValue(
                    candidate.Node,
                    out var entryConnectorDistance) ||
                !roadDistances.TryGetValue(
                    candidate.Node,
                    out var roadDistance) ||
                !IsFirstRoadTargetSafe(
                    entryTile,
                    path.First,
                    pathFindingParams))
            {
                continue;
            }

            routes.Add(new Route(
                entryRoadNode,
                candidate.Node,
                entryTile,
                candidate.TerrainTile,
                entryConnectorDistance,
                candidate.ConnectorDistance,
                path,
                roadDistance,
                candidate.Cost));

            if (routes.Count >= MaxRouteCandidates)
            {
                break;
            }
        }

        _ = vehicle;
        return routes.Count > 0;
    }

    internal bool ShouldUseHighway(
        Fix32 directCost,
        Fix32 entryCost,
        Fix32 roadDistance,
        Fix32 exitCost,
        out Fix32 highwayCost)
    {
        highwayCost = entryCost +
            ComputeHighwayTravelCost(roadDistance) + exitCost;
        return ShouldPreferHighway(directCost, highwayCost);
    }

    internal bool IsRouteStillUsable(
        Route route,
        VehiclePathFindingParams pathFindingParams)
    {
        if (route.RoadPath.IsEmpty ||
            !ContainsHighwaySegment(route.RoadPath) ||
            !IsContinuousAndPathable(route.RoadPath))
        {
            return false;
        }

        var first = route.RoadPath.First;
        first.Entity.GetLaneNodes(
            first.LaneIndex,
            out var entryNode,
            out _);
        var last = route.RoadPath.Last;
        last.Entity.GetLaneNodes(
            last.LaneIndex,
            out _,
            out var exitNode);

        return pathFindingParams.CanUseRoadType(entryNode.LaneType) &&
            pathFindingParams.CanUseRoadType(exitNode.LaneType) &&
            entryNode == route.EntryRoadNode &&
            exitNode == route.ExitRoadNode;
    }

    private Dictionary<RoadGraphNodeKey, List<HighwayEdge>> GetGraph()
    {
        if (!m_graphIsDirty && m_cachedGraph != null)
        {
            return m_cachedGraph;
        }

        var graph = new Dictionary<RoadGraphNodeKey, List<HighwayEdge>>();
        var entryAccessNodes = new HashSet<RoadGraphNodeKey>();
        var exitAccessNodes = new HashSet<RoadGraphNodeKey>();
        var skippedLongLanes = 0;
        var skippedLongEntities = 0;
        foreach (var entity in
                 m_entitiesManager.GetAllEntitiesOfType<RoadEntityBase>())
        {
            if (entity.RoadProto is not IHighwayNetworkProto networkProto ||
                !networkProto.ParticipatesInHighwayNetwork ||
                !entity.CanPathfindThrough())
            {
                continue;
            }

            // Admit all directed lanes of a two-way highway entity together.
            // Otherwise the slightly longer outside chord of a curve could
            // remove only the return lane and silently create a one-way gap.
            var hasUnsafeApproach = false;
            for (var laneIndex = 0;
                 laneIndex < entity.RoadLanesCount;
                laneIndex++)
            {
                entity.GetLaneNodes(laneIndex, out var start, out var end);
                // PathFindingEntity rejects the first road target at 24+
                // tiles using the straight distance from the vehicle to the
                // lane endpoint. A curved lane's arc length can be longer in
                // only one direction and must not remove that directed lane
                // from the graph. Keep arc length solely as routing cost.
                var approachDistance =
                    start.Position2f.DistanceTo(end.Position2f);
                if (approachDistance >
                    MaxSafeRoadApproachTiles.Tiles().Value)
                {
                    hasUnsafeApproach = true;
                    break;
                }
            }

            if (hasUnsafeApproach)
            {
                skippedLongLanes += entity.RoadLanesCount;
                skippedLongEntities++;
                continue;
            }

            if (entity.RoadProto is IHighwayPortProto portProto)
            {
                AddTerrainAccessNodes(
                    portProto,
                    entity.Transform,
                    entryAccessNodes,
                    exitAccessNodes);
            }

            for (var laneIndex = 0;
                 laneIndex < entity.RoadLanesCount;
                laneIndex++)
            {
                entity.GetLaneNodes(laneIndex, out var start, out var end);
                var laneDistance = entity.RoadProto.LanesData[laneIndex]
                    .LaneLength.Value;
                var edge = new HighwayEdge(
                    start,
                    end,
                    new RoadPathSegment(entity, laneIndex),
                    laneDistance);
                if (!graph.TryGetValue(start, out var outgoing))
                {
                    outgoing = new List<HighwayEdge>();
                    graph.Add(start, outgoing);
                }

                outgoing.Add(edge);
            }
        }

        if (skippedLongLanes > 0)
        {
            Log.Warning(
                $"GroundRoads: ignored {skippedLongLanes} highway lanes " +
                $"in {skippedLongEntities} complete segments: at least one " +
                $"endpoint distance exceeds {MaxSafeRoadApproachTiles} " +
                "tiles and would make the first native road target unsafe.");
        }

        m_cachedGraph = graph;
        m_entryAccessNodes = entryAccessNodes;
        m_exitAccessNodes = exitAccessNodes;
        m_graphIsDirty = false;
        return m_cachedGraph;
    }

    private static void AddTerrainAccessNodes(
        IHighwayPortProto portProto,
        TileTransform transform,
        HashSet<RoadGraphNodeKey> entryAccessNodes,
        HashSet<RoadGraphNodeKey> exitAccessNodes)
    {
        for (var index = 0; index < portProto.HighwayPortCount; index++)
        {
            var port = portProto.GetHighwayPort(index, transform);
            entryAccessNodes.Add(port.InboundNode);
            exitAccessNodes.Add(port.OutboundNode);
        }
    }

    private void OnRoadChanged(IRoadGraphEntity entity)
    {
        if (entity.RoadProto is IHighwayNetworkProto networkProto &&
            networkProto.ParticipatesInHighwayNetwork)
        {
            m_graphIsDirty = true;
        }
    }

    private static bool ContainsHighwaySegment(
        ImmutableArray<RoadPathSegment> path)
    {
        foreach (var segment in path)
        {
            if (segment.Entity.RoadProto is IHighwayMainlineProto mainline &&
                mainline.ParticipatesInHighwayNetwork)
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<RoadPathSegment> ReconstructPath(
        RoadGraphNodeKey exit,
        RoadGraphNodeKey entry,
        Dictionary<RoadGraphNodeKey, PreviousStep> previous)
    {
        var reversed = new List<RoadPathSegment>();
        var current = exit;
        var remainingGuard = previous.Count + 1;
        while (current != entry)
        {
            if (remainingGuard-- <= 0 ||
                !previous.TryGetValue(current, out var step))
            {
                return ImmutableArray<RoadPathSegment>.Empty;
            }

            reversed.Add(step.Edge.Segment);
            current = step.Node;
        }

        reversed.Reverse();
        var builder =
            new ImmutableArrayBuilder<RoadPathSegment>(reversed.Count);
        for (var index = 0; index < reversed.Count; index++)
        {
            builder[index] = reversed[index];
        }

        return builder.GetImmutableArrayAndClear();
    }

    private static bool IsContinuousAndPathable(
        ImmutableArray<RoadPathSegment> path)
    {
        var hasPrevious = false;
        var previousEnd = default(RoadGraphNodeKey);
        foreach (var segment in path)
        {
            if (segment.Entity.IsDestroyed ||
                !segment.Entity.CanPathfindThrough())
            {
                return false;
            }

            segment.Entity.GetLaneNodes(
                segment.LaneIndex,
                out var start,
                out var end);
            if (hasPrevious && previousEnd != start)
            {
                return false;
            }

            hasPrevious = true;
            previousEnd = end;
        }

        return hasPrevious;
    }

    private static Fix32 ComputeHighwayTravelCost(Fix32 roadDistance)
    {
        return roadDistance * 100 / HighwaySpeedPercent;
    }

    private static Tile2i ProjectHighwayNodeToTerrain(
        RoadGraphNodeKey node,
        VehiclePathFindingParams pathFindingParams)
    {
        // Round through the vehicle's native center-space convention before
        // resolving a nearby terrain access. Z is validated separately so an
        // elevated ramp seam can never become a vertical vehicle teleport.
        return pathFindingParams.RoundCenterSpace(node.Position2f);
    }

    private static bool IsTerrainHeightReachable(
        HeightTilesF roadHeight,
        HeightTilesF terrainHeight)
    {
        // Planner anchors and exact lane endpoints may use half-tile heights.
        // This matches the native road-entry height tolerance.
        return (roadHeight - terrainHeight).Abs <=
               0.5.TilesThick();
    }

    private static bool AreTerrainAccessHeightsReachable(
        HeightTilesF roadHeight,
        HeightTilesF endpointTerrainHeight,
        HeightTilesF accessTerrainHeight)
    {
        return IsTerrainHeightReachable(
                   roadHeight,
                   endpointTerrainHeight) &&
               IsTerrainHeightReachable(
                   roadHeight,
                   accessTerrainHeight);
    }

    private static bool TryGetExactRoadNodeHeight(
        RoadGraphNodeKey node,
        RoadPathSegment adjacentRoadSegment,
        out HeightTilesF height)
    {
        var entity = adjacentRoadSegment.Entity;
        entity.GetLaneNodes(
            adjacentRoadSegment.LaneIndex,
            out var start,
            out var end);
        var lane = entity.GetTransformedRoadLane(
            adjacentRoadSegment.LaneIndex);
        var laneOrigin = entity.CenterTile.CornerTile3f;
        if (node == start)
        {
            height = (laneOrigin + lane.LaneCenterSamples.First).Height;
            return true;
        }

        if (node == end)
        {
            height = (laneOrigin + lane.LaneCenterSamples.Last).Height;
            return true;
        }

        height = default;
        return false;
    }

    private bool TryResolveTerrainAccess(
        RoadGraphNodeKey node,
        VehiclePathFindingParams pathFindingParams,
        RoadPathSegment adjacentRoadSegment,
        bool requireSafeFirstTarget,
        out Tile2i accessTile)
    {
        // A terrain transition is only safe at a horizontal road seam. Ramp
        // start/end pieces provide G0 nodes at their feet and crests; their
        // inclined internal seams remain road-only even when a hillside is
        // close enough to pass the height tolerance below.
        if (node.Direction.GradeFactor != TrainTrackGradeFactor.G0)
        {
            accessTile = default;
            return false;
        }

        if (!TryGetExactRoadNodeHeight(
                node,
                adjacentRoadSegment,
                out var exactRoadHeight))
        {
            accessTile = default;
            return false;
        }

        // Nearby recovery tiles are useful when the road footprint blocks
        // the projected pathfinding tile, but they must never mask a road end
        // that is floating above or buried below its own local terrain.
        var endpointTerrainHeight =
            m_terrainManager.GetHeight(node.Position2f);

        bool isAcceptable(Tile2i tile)
        {
            var cornerTile =
                pathFindingParams.ConvertToCornerTileSpace(tile);
            var terrainPosition =
                pathFindingParams.ConvertToCenterTileSpace(cornerTile);
            return AreTerrainAccessHeightsReachable(
                       exactRoadHeight,
                       endpointTerrainHeight,
                       m_terrainManager.GetHeight(terrainPosition)) &&
                   m_pathabilityProvider.IsPathableRaw(
                cornerTile,
                pathFindingParams.PathabilityQueryMask) &&
                (!requireSafeFirstTarget ||
                 IsFirstRoadTargetSafe(
                     tile,
                     adjacentRoadSegment,
                     pathFindingParams));
        }

        var projectedTile = ProjectHighwayNodeToTerrain(
            node,
            pathFindingParams);
        if (isAcceptable(projectedTile))
        {
            accessTile = projectedTile;
            return true;
        }

        // Search the same local radius as native start-tile recovery. Prefer
        // the right/outside of the directed lane so a connector does not cross
        // the median or opposing lane. If that side is blocked, fall back to
        // the closest valid point on any side.
        var maxRadius = pathFindingParams.MinSizeClearance.Value + 2;
        var direction = node.Direction.Direction;

        bool tryRing(
            int radius,
            bool outwardOnly,
            out Tile2i selectedTile)
        {
            var found = false;
            var bestScore = int.MinValue;
            var bestDistanceSqr = int.MaxValue;
            var bestTile = default(Tile2i);

            void consider(int dx, int dy)
            {
                // Dot(offset, rightOrthogonal(direction)).
                var outwardScore = dx * direction.Y -
                    dy * direction.X;
                if (outwardOnly && outwardScore <= 0)
                {
                    return;
                }

                var candidate = projectedTile + new RelTile2i(dx, dy);
                if (!isAcceptable(candidate))
                {
                    return;
                }

                var distanceSqr = dx * dx + dy * dy;
                if (!found || outwardScore > bestScore ||
                    (outwardScore == bestScore &&
                     distanceSqr < bestDistanceSqr))
                {
                    found = true;
                    bestScore = outwardScore;
                    bestDistanceSqr = distanceSqr;
                    bestTile = candidate;
                }
            }

            for (var offset = -radius; offset <= radius; offset++)
            {
                consider(-radius, offset);
                consider(radius, offset);
            }

            for (var offset = -radius + 1;
                 offset < radius;
                offset++)
            {
                consider(offset, -radius);
                consider(offset, radius);
            }

            selectedTile = bestTile;
            return found;
        }

        for (var radius = 1; radius <= maxRadius; radius++)
        {
            if (tryRing(radius, outwardOnly: true, out accessTile))
            {
                return true;
            }
        }

        for (var radius = 1; radius <= maxRadius; radius++)
        {
            if (tryRing(radius, outwardOnly: false, out accessTile))
            {
                return true;
            }
        }

        accessTile = default;
        return false;
    }

    private static Fix32 ComputeConnectorDistance(
        Tile2i terrainAccessTile,
        RoadGraphNodeKey roadNode,
        VehiclePathFindingParams pathFindingParams)
    {
        return GetTerrainAccessPosition(
                terrainAccessTile,
                pathFindingParams).DistanceTo(
            roadNode.Position2f);
    }

    private static bool IsFirstRoadTargetSafe(
        Tile2i entryAccessTile,
        RoadPathSegment first,
        VehiclePathFindingParams pathFindingParams)
    {
        first.Entity.GetLaneNodes(
            first.LaneIndex,
            out _,
            out var firstTarget);
        return GetTerrainAccessPosition(
                entryAccessTile,
                pathFindingParams).DistanceTo(
            firstTarget.Position2f) <=
            MaxSafeRoadApproachTiles.Tiles().Value;
    }

    private static Tile2f GetTerrainAccessPosition(
        Tile2i accessTile,
        VehiclePathFindingParams pathFindingParams)
    {
        var cornerTile =
            pathFindingParams.ConvertToCornerTileSpace(accessTile);
        return pathFindingParams.ConvertToCenterTileSpace(cornerTile);
    }

    private static bool ShouldPreferHighway(
        Fix32 directCost,
        Fix32 highwayCost)
    {
        return highwayCost * 100 <=
            directCost * HighwayPreferencePercent;
    }

    private static Fix32 MinimumDistance(
        IIndexable<Tile2i> tiles,
        Tile2i target)
    {
        var best = Fix32.MaxValue;
        foreach (var tile in tiles)
        {
            best = best.Min(tile.DistanceTo(target));
        }

        return best;
    }
}
