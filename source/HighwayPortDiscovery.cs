using System.Collections.Generic;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Entities;
using Mafi.Core.Roads;

namespace GroundRoads;

internal static class HighwayPortDiscovery
{
    /// <summary>
    /// Collects physical ports which currently have no exact two-way mate.
    /// Must be called from the simulation synchronization barrier.
    /// </summary>
    public static void FillOpenPortsNear(
        IEntitiesManager entitiesManager,
        Tile2i center,
        int rangeTiles,
        bool includeJunctionPorts,
        Lyst<HighwayPort> result)
    {
        FillOpenPortsCore(
            entitiesManager,
            center,
            rangeTiles,
            includeJunctionPorts,
            result);
    }

    /// <summary>
    /// Collects every physical port which currently has no exact two-way
    /// mate. Callers that need several spatial views can enumerate the road
    /// network once and filter this result instead of repeating discovery.
    /// Must be called from the simulation synchronization barrier.
    /// </summary>
    public static void FillOpenPorts(
        IEntitiesManager entitiesManager,
        bool includeJunctionPorts,
        Lyst<HighwayPort> result)
    {
        FillOpenPortsCore(
            entitiesManager,
            center: null,
            rangeTiles: 0,
            includeJunctionPorts,
            result);
    }

    private static void FillOpenPortsCore(
        IEntitiesManager entitiesManager,
        Tile2i? center,
        int rangeTiles,
        bool includeJunctionPorts,
        Lyst<HighwayPort> result)
    {
        var candidatePorts = new List<HighwayPort>();
        var portCounts = new Dictionary<HighwayPortKey, int>();
        foreach (var entity in
                 entitiesManager.GetAllEntitiesOfType<RoadEntityBase>())
        {
            if (entity.RoadProto is not IHighwayPortProto portProto ||
                entity.IsDestroyed)
            {
                continue;
            }

            var isCandidate = IsEligiblePortSource(
                entity.RoadProto,
                includeJunctionPorts);
            for (var index = 0; index < portProto.HighwayPortCount; index++)
            {
                var port = portProto.GetHighwayPort(index, entity.Transform);
                if (isCandidate)
                {
                    candidatePorts.Add(port);
                }

                // Every highway port participates in occupancy counting,
                // including junction ports that are not placement targets.
                // Otherwise the segment below an existing junction would be
                // reported as open and a second node could overlap it.
                var key = new HighwayPortKey(port);
                portCounts.TryGetValue(key, out var count);
                portCounts[key] = count + 1;
            }
        }

        result.Clear();
        foreach (var port in candidatePorts)
        {
            if (portCounts[new HighwayPortKey(port)] == 1 &&
                (!center.HasValue ||
                 port.Center.Xy.IsNear(center.Value, rangeTiles)))
            {
                result.Add(port);
            }
        }
    }

    private static bool IsEligiblePortSource(
        RoadEntityProtoBase roadProto,
        bool includeJunctionPorts)
    {
        return roadProto is IHighwayPortProto &&
            (includeJunctionPorts || roadProto is not HighwayJunctionProto);
    }
}
