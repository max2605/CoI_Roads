using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Gfx;
using Mafi.Core.Roads;
using Mafi.Core.Trains;
using Mafi.Numerics;
using Mafi.Unity;
using Mafi.Unity.Entities;
using Mafi.Unity.Entities.Static;
using UnityEngine;

namespace GroundRoads;

public sealed class GroundRoadModelFactory :
    IProtoModelFactory<GroundRoadProto>,
    IProtoModelFactory<GroundRoadEntranceProto>,
    IProtoModelFactory<GroundRoadAccessibleSegmentProto>,
    IProtoModelFactory<HighwaySegmentProto>,
    IProtoModelFactory<HighwayJunctionProto>,
    IProtoModelFactory<HighwayOnRampProto>,
    IProtoModelFactory<HighwayOffRampProto>
{
    private const string VertexColorMaterial =
        "Assets/Core/Materials/VertexColor.mat";
    private const float RoadSurfaceTopTiles = 0.03f;
    private const float RoadSurfaceBottomTiles = -0.02f;

    private static readonly ColorRgba AsphaltColor =
        new(38, 43, 48, 255);

    private static readonly ColorRgba EdgeLineColor =
        new(235, 235, 220, 255);

    private static readonly Color32 SupportColumnColor =
        new(87, 84, 78, 255);

    private static readonly Color32 SupportBeamColor =
        new(108, 104, 96, 255);

    private readonly AssetsDb m_assetsDb;

    public GroundRoadModelFactory(AssetsDb assetsDb)
    {
        m_assetsDb = assetsDb;
    }

    public GameObject Create(GroundRoadProto proto)
    {
        return CreateModel(proto);
    }

    public GameObject Create(GroundRoadEntranceProto proto)
    {
        return CreateModel(proto);
    }

    public GameObject Create(GroundRoadAccessibleSegmentProto proto)
    {
        return CreateModel(proto);
    }

    public GameObject Create(HighwaySegmentProto proto)
    {
        return CreateModel(proto);
    }

    public GameObject Create(HighwayJunctionProto proto)
    {
        return CreateModel(proto);
    }

    public GameObject Create(HighwayOnRampProto proto)
    {
        return CreateModel(proto);
    }

    public GameObject Create(HighwayOffRampProto proto)
    {
        return CreateModel(proto);
    }

    private GameObject CreateModel(RoadEntityProtoBase proto)
    {
        var gameObject = new GameObject(proto.Id.Value);
        var builder = MeshBuilder.Instance;
        var modelOffset =
            -(proto.Layout.GetCenter(TileTransform.Identity) -
              proto.Graphics.PrefabOrigin).ToVector3();

        builder.SetTransform(modelOffset);

        if (proto is HighwaySegmentProto highwaySegment)
        {
            AppendHighwaySupports(builder, highwaySegment);
        }

        if (proto is HighwayJunctionProto junction)
        {
            // Direct junction arms use a broad central asphalt union so their
            // turning curves never leave the visible road. The roundabout's
            // visual list contains each shared physical lane exactly once.
            var visualWidth =
                (junction.Kind == HighwayNodeKind.Roundabout ? 2.0 : 8.0)
                .Tiles()
                .ToUnityUnits();
            foreach (var trajectory in junction.VisualRoadTrajectories)
            {
                AppendAsphaltSurface(
                    builder,
                    trajectory,
                    visualWidth);
            }
        }
        else
        {
            for (var laneIndex = 0;
                 laneIndex < proto.LanesTrajectories.Length;
                 laneIndex++)
            {
                // The logical MaskAllowAll lane is four tiles wide so every
                // truck class may use it. Visually a highway lane is only two
                // tiles wide; otherwise two overlapping four-tile strips look
                // like a three-lane road.
                var visualWidth = proto is IHighwayNetworkProto
                    ? 2.0.Tiles().ToUnityUnits()
                    : proto.LanesSpecs[laneIndex].GetWidth().ToUnityUnits();
                AppendVisibleLane(
                    builder,
                    proto.LanesTrajectories[laneIndex],
                    visualWidth);
            }
        }

        builder.UpdateGoAndClear(
            gameObject,
            m_assetsDb.GetSharedMaterial(VertexColorMaterial));

        LayoutEntityModelFactory.AddLayoutBoxCollider(gameObject, proto);
        return gameObject;
    }

    private static void AppendVisibleLane(
        MeshBuilder builder,
        RoadLaneTrajectory trajectory,
        float width)
    {
        if (trajectory.LaneCenterSamples.IsEmpty)
        {
            return;
        }

        var tile = 1.0.Tiles().ToUnityUnits();
        var surfaceTop = RoadSurfaceTopTiles * tile;
        var surfaceBottom = RoadSurfaceBottomTiles * tile;
        var markerWidth = 0.11f * tile;
        var markerTop = surfaceTop + 0.006f * tile;
        var markerOffset = width / 2f - markerWidth;

        AppendStrip(
            builder,
            trajectory,
            CreateStripCrossSection(
                width,
                lateralOffset: 0f,
                surfaceBottom,
                surfaceTop,
                AsphaltColor));
        AppendStrip(
            builder,
            trajectory,
            CreateStripCrossSection(
                markerWidth,
                -markerOffset,
                surfaceTop,
                markerTop,
                EdgeLineColor));
        AppendStrip(
            builder,
            trajectory,
            CreateStripCrossSection(
                markerWidth,
                markerOffset,
                surfaceTop,
                markerTop,
                EdgeLineColor));
    }

    private static void AppendAsphaltSurface(
        MeshBuilder builder,
        RoadLaneTrajectory trajectory,
        float width)
    {
        var tile = 1.0.Tiles().ToUnityUnits();
        var surfaceTop = RoadSurfaceTopTiles * tile;
        var surfaceBottom = RoadSurfaceBottomTiles * tile;
        AppendStrip(
            builder,
            trajectory,
            CreateStripCrossSection(
                width,
                lateralOffset: 0f,
                surfaceBottom,
                surfaceTop,
                AsphaltColor));
    }

    private static void AppendHighwaySupports(
        MeshBuilder builder,
        HighwaySegmentProto proto)
    {
        // These are the same candidate locations the train-track prototypes
        // expose to their native pillar builder. Native pillar entities cannot
        // be parented to a road without also registering that road as a train
        // track, so the highway renders its own concrete support geometry at
        // those positions instead.
        var candidates = proto.SourceTrackProto.TrainTrackHelper
            .TransformedPillarLocationsCache[0];
        var tile = 1.0.Tiles().ToUnityUnits();
        var maximumDepth =
            TrainTrackPillarProto.MAX_PILLAR_HEIGHT.Value * tile;
        var beamHalfThickness = 0.10f * tile;
        var beamHalfWidth = 2.10f * tile;
        var columnHalfWidth = 0.28f * tile;
        var baseHalfWidth = 0.46f * tile;
        var baseHalfHeight = 0.16f * tile;

        var appendedSupport = false;
        for (var index = 0; index < candidates.Length; index++)
        {
            if (!ShouldAppendSupportAtBlock(index, candidates.Length))
            {
                continue;
            }

            var candidate = candidates[index];
            if (!TryGetRoadDeckCenter(
                    proto,
                    candidate.Position,
                    out var roadCenter))
            {
                continue;
            }

            var forward = new RelTile3f(
                    candidate.Direction,
                    Fix32.Zero)
                .ToVector3NoUnitConversion()
                .normalized;
            if (forward.sqrMagnitude <= 0.0001f)
            {
                continue;
            }

            AppendHighwaySupport(
                builder,
                roadCenter,
                forward,
                maximumDepth,
                tile,
                beamHalfThickness,
                beamHalfWidth,
                columnHalfWidth,
                baseHalfWidth,
                baseHalfHeight);
            appendedSupport = true;
        }

        // Very short planner pieces can intentionally omit native train
        // pillar candidates because their neighbouring track pieces normally
        // carry them. A highway segment can also be placed in isolation, so
        // give such a piece one centered support instead of letting it float.
        if (!appendedSupport &&
            TryGetFallbackSupportFrame(
                proto,
                out var fallbackCenter,
                out var fallbackForward))
        {
            AppendHighwaySupport(
                builder,
                fallbackCenter,
                fallbackForward,
                maximumDepth,
                tile,
                beamHalfThickness,
                beamHalfWidth,
                columnHalfWidth,
                baseHalfWidth,
                baseHalfHeight);
        }
    }

    private static bool ShouldAppendSupportAtBlock(
        int blockIndex,
        int blockCount)
    {
        // Do not thin this list: TrainTrackHelper has already selected the
        // native railway pillar candidates. Keeping every valid entry makes
        // highway support spacing follow the source track exactly.
        return blockCount > 0 &&
               blockIndex >= 0 &&
               blockIndex < blockCount;
    }

    private static bool TryGetRoadDeckCenter(
        HighwaySegmentProto proto,
        RelTile2f supportPosition,
        out Vector3 roadCenter)
    {
        roadCenter = new RelTile3f(
                supportPosition,
                Fix32.Zero)
            .ToVector3();
        if (proto.LanesTrajectories.IsEmpty ||
            proto.LanesTrajectories[0].LaneCenterSamples.IsEmpty)
        {
            return false;
        }

        var samples = proto.LanesTrajectories[0].LaneCenterSamples;
        var bestDistanceSquared = float.MaxValue;
        var bestHeight = samples[0].ToVector3().y;
        for (var index = 1; index < samples.Length; index++)
        {
            var start = samples[index - 1].ToVector3();
            var end = samples[index].ToVector3();
            var horizontal = end - start;
            horizontal.y = 0f;
            var lengthSquared = horizontal.sqrMagnitude;
            var fromStart = roadCenter - start;
            fromStart.y = 0f;
            var progress = lengthSquared <= 0.000001f
                ? 0f
                : Mathf.Clamp01(
                    Vector3.Dot(fromStart, horizontal) / lengthSquared);
            var closest = start + horizontal * progress;
            var difference = roadCenter - closest;
            difference.y = 0f;
            var distanceSquared = difference.sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            bestHeight = Mathf.Lerp(start.y, end.y, progress);
        }

        roadCenter.y = bestHeight;
        return true;
    }

    private static bool TryGetFallbackSupportFrame(
        HighwaySegmentProto proto,
        out Vector3 roadCenter,
        out Vector3 forward)
    {
        roadCenter = default;
        forward = default;
        if (proto.LanesTrajectories.Length < 2)
        {
            return false;
        }

        var firstLane = proto.LanesTrajectories[0];
        var secondLane = proto.LanesTrajectories[1];
        if (firstLane.LaneCenterSamples.IsEmpty ||
            secondLane.LaneCenterSamples.IsEmpty ||
            firstLane.LaneDirectionSamples.IsEmpty)
        {
            return false;
        }

        roadCenter = (GetTrajectoryMidpoint(firstLane) +
                      GetTrajectoryMidpoint(secondLane)) * 0.5f;
        forward = firstLane.LaneDirectionSamples[
                firstLane.LaneDirectionSamples.Length / 2]
            .ToVector3NoUnitConversion()
            .normalized;
        return forward.sqrMagnitude > 0.0001f;
    }

    private static Vector3 GetTrajectoryMidpoint(
        RoadLaneTrajectory trajectory)
    {
        var samples = trajectory.LaneCenterSamples;
        var upperIndex = samples.Length / 2;
        if ((samples.Length & 1) != 0)
        {
            return samples[upperIndex].ToVector3();
        }

        return (samples[upperIndex - 1].ToVector3() +
                samples[upperIndex].ToVector3()) * 0.5f;
    }

    private static void AppendHighwaySupport(
        MeshBuilder builder,
        Vector3 roadCenter,
        Vector3 forward,
        float maximumDepth,
        float tile,
        float beamHalfThickness,
        float beamHalfWidth,
        float columnHalfWidth,
        float baseHalfWidth,
        float baseHalfHeight)
    {
        var lateral = Vector3.Cross(Vector3.up, forward).normalized;
        var beamCenter = roadCenter + Vector3.up *
            (RoadSurfaceBottomTiles * tile - beamHalfThickness);
        builder.AddBox(
            beamCenter - lateral * beamHalfWidth,
            beamCenter + lateral * beamHalfWidth,
            beamHalfThickness,
            SupportBeamColor);

        var columnTop = beamCenter - Vector3.up * beamHalfThickness;
        var columnBottom = roadCenter - Vector3.up * maximumDepth;
        builder.AddBox(
            columnBottom,
            columnTop,
            columnHalfWidth,
            SupportColumnColor);
        builder.AddAaBox(
            columnBottom + Vector3.up * baseHalfHeight,
            new Vector3(
                baseHalfWidth,
                baseHalfHeight,
                baseHalfWidth),
            SupportColumnColor,
            BoxFaceMask.All);
    }

    private static void AppendStrip(
        MeshBuilder builder,
        RoadLaneTrajectory trajectory,
        ImmutableArray<CrossSection> crossSection)
    {
        var samples = trajectory.LaneCenterSamples;
        var directions = trajectory.LaneDirectionSamples;

        builder.StartExtrusion(
            crossSection,
            samples[0].ToVector3(),
            directions[0].ToVector3NoUnitConversion(),
            Vector3.zero,
            uvXOffset: 0f);

        for (var index = 1; index < samples.Length; index++)
        {
            builder.ContinueExtrusion(
                crossSection,
                samples[index].ToVector3(),
                directions[index].ToVector3NoUnitConversion(),
                Vector3.zero,
                trajectory.SegmentLengthsPrefixSums[index].ToUnityUnits() *
                0.2f);
        }
    }

    private static ImmutableArray<CrossSection> CreateStripCrossSection(
        float width,
        float lateralOffset,
        float bottom,
        float top,
        ColorRgba color)
    {
        var halfWidth = width / 2f;
        var normal = new Vector2Float(0f, 1f);

        return ImmutableArray.Create(
            new CrossSection(
                isClockwise: true,
                ImmutableArray.Create(
                    new CrossSectionVertexFloat(
                        new Vector2Float(
                            lateralOffset - halfWidth,
                            bottom),
                        normal,
                        0f,
                        color),
                    new CrossSectionVertexFloat(
                        new Vector2Float(
                            lateralOffset - halfWidth,
                            top),
                        normal,
                        0.02f,
                        color),
                    new CrossSectionVertexFloat(
                        new Vector2Float(
                            lateralOffset + halfWidth,
                            top),
                        normal,
                        0.98f,
                        color),
                    new CrossSectionVertexFloat(
                        new Vector2Float(
                            lateralOffset + halfWidth,
                            bottom),
                        normal,
                        1f,
                        color))));
    }
}
