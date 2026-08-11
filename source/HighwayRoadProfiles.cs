using System;
using Mafi;
using Mafi.Collections.ImmutableCollections;

namespace GroundRoads;

/// <summary>
/// Save-stable highway families. Existing Standard prototypes deliberately
/// keep their historical IDs and geometry; wider families always use new IDs.
/// </summary>
public enum HighwayTier
{
    Standard = 2,
    HeavyT3 = 3,
    MassiveT4 = 4
}

public sealed class HighwayRoadProfile
{
    public static readonly HighwayRoadProfile Standard = new(
        HighwayTier.Standard,
        idToken: string.Empty,
        laneOffsetsTiles: ImmutableArray.Create(1.0.ToFix32()),
        visualLaneWidthTiles: 2.0,
        roadHalfWidthTiles: 2.0,
        constructionWidthScale: 1.0,
        toolTextId: "tool.highway");

    public static readonly HighwayRoadProfile HeavyT3 = new(
        HighwayTier.HeavyT3,
        idToken: "T3HeavyV1_",
        laneOffsetsTiles: ImmutableArray.Create(2.0.ToFix32()),
        visualLaneWidthTiles: 4.0,
        roadHalfWidthTiles: 4.0,
        constructionWidthScale: 2.0,
        toolTextId: "tool.highway-t3");

    public static readonly HighwayRoadProfile MassiveT4 = new(
        HighwayTier.MassiveT4,
        idToken: "T4MassiveV1_",
        // Pair order is compatibility-friendly: forward/reverse inner lanes,
        // then forward/reverse outer lanes. Inner is the passing lane.
        laneOffsetsTiles: ImmutableArray.Create(
            2.0.ToFix32(),
            6.0.ToFix32()),
        visualLaneWidthTiles: 4.0,
        roadHalfWidthTiles: 8.0,
        constructionWidthScale: 4.0,
        toolTextId: "tool.highway-t4");

    public static readonly ImmutableArray<HighwayRoadProfile> All =
        ImmutableArray.Create(Standard, HeavyT3, MassiveT4);

    public HighwayTier Tier { get; }

    public string IdToken { get; }

    public ImmutableArray<Fix32> LaneOffsetsTiles { get; }

    public int LanesPerDirection => LaneOffsetsTiles.Length;

    public double VisualLaneWidthTiles { get; }

    public double RoadHalfWidthTiles { get; }

    public double ConstructionWidthScale { get; }

    public string ToolTextId { get; }

    private HighwayRoadProfile(
        HighwayTier tier,
        string idToken,
        ImmutableArray<Fix32> laneOffsetsTiles,
        double visualLaneWidthTiles,
        double roadHalfWidthTiles,
        double constructionWidthScale,
        string toolTextId)
    {
        Tier = tier;
        IdToken = idToken;
        LaneOffsetsTiles = laneOffsetsTiles;
        VisualLaneWidthTiles = visualLaneWidthTiles;
        RoadHalfWidthTiles = roadHalfWidthTiles;
        ConstructionWidthScale = constructionWidthScale;
        ToolTextId = toolTextId;
    }

    public bool IsPassingLane(int laneIndex)
    {
        return Tier == HighwayTier.MassiveT4 &&
               laneIndex >= 0 &&
               laneIndex < 2;
    }

    public static HighwayRoadProfile Get(HighwayTier tier)
    {
        foreach (var profile in All)
        {
            if (profile.Tier == tier)
            {
                return profile;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(tier));
    }
}

public interface IHighwayLaneProfileProto : IHighwayNetworkProto
{
    HighwayTier Tier { get; }

    int LanesPerDirection { get; }

    double VisualLaneWidthTiles { get; }

    double RoadHalfWidthTiles { get; }

    bool IsPassingLane(int laneIndex);
}
