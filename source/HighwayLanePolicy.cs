using Mafi;
using Mafi.Core.Entities.Dynamic;
using Mafi.Core.Roads;

namespace GroundRoads;

internal enum HighwayLanePreference
{
    Driving,
    Passing
}

/// <summary>
/// Applies a deliberately small, static route-cost bias to T4's parallel
/// lanes. It does not perform a live lane change: the selected lane remains a
/// normal part of the native road path.
/// </summary>
internal static class HighwayLanePolicy
{
    // A vehicle within 20% of the fastest road-capable prototype belongs to
    // the fast cohort. Current speed is intentionally irrelevant: congestion
    // must not make a normally fast vehicle switch its static route class.
    private const int FastVehicleThresholdPercent = 80;
    private const int NonPreferredLaneCostPercent = 105;

    internal static bool IsManagedProfile(
        IRoadGraphEntityProto roadProto,
        out IHighwayLaneProfileProto profile)
    {
        profile = roadProto as IHighwayLaneProfileProto;
        return profile != null &&
            profile.Tier == HighwayTier.MassiveT4 &&
            profile.LanesPerDirection >= 2;
    }

    internal static HighwayLanePreference SelectPreference(
        IPathFindingVehicle vehicle,
        RelTile1f vehicleMaxRoadSpeed,
        RelTile1f fastestRoadVehicleSpeed)
    {
        if (vehicle?.Prototype == null ||
            fastestRoadVehicleSpeed.Value.IsNotPositive)
        {
            return HighwayLanePreference.Driving;
        }

        var vehicleSpeed = vehicleMaxRoadSpeed.Value;
        if (vehicleSpeed.IsNotPositive)
        {
            return HighwayLanePreference.Driving;
        }

        return vehicleSpeed * 100 >=
               fastestRoadVehicleSpeed.Value * FastVehicleThresholdPercent
            ? HighwayLanePreference.Passing
            : HighwayLanePreference.Driving;
    }

    internal static Fix32 NormalizeParallelLaneDistance(
        IRoadGraphEntityProto roadProto,
        Fix32 fallbackDistance)
    {
        if (!IsManagedProfile(roadProto, out _) ||
            roadProto.LanesData.IsEmpty)
        {
            return fallbackDistance;
        }

        // Parallel lanes on a curve have different arc lengths even though
        // they represent the same mainline distance. Their mean is the stable
        // centre-line cost and prevents curve handedness from overriding the
        // intended driving/passing-lane preference.
        var sum = Fix32.Zero;
        foreach (var lane in roadProto.LanesData)
        {
            sum += lane.LaneLength.Value;
        }

        return sum / roadProto.LanesData.Length;
    }

    internal static Fix32 ApplyPreferenceCost(
        IRoadGraphEntityProto roadProto,
        int laneIndex,
        HighwayLanePreference preference,
        Fix32 travelCost)
    {
        if (!IsManagedProfile(roadProto, out var profile) ||
            laneIndex < 0 || laneIndex >= roadProto.LanesData.Length)
        {
            return travelCost;
        }

        var wantsPassingLane =
            preference == HighwayLanePreference.Passing;
        return profile.IsPassingLane(laneIndex) == wantsPassingLane
            ? travelCost
            : travelCost * NonPreferredLaneCostPercent / 100;
    }
}
