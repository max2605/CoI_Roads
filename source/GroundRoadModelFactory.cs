using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Gfx;
using Mafi.Core.Roads;
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

    private static readonly ColorRgba AsphaltColor =
        new(38, 43, 48, 255);

    private static readonly ColorRgba EdgeLineColor =
        new(235, 235, 220, 255);

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
        var surfaceTop = 0.03f * tile;
        var surfaceBottom = -0.02f * tile;
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
        var surfaceTop = 0.03f * tile;
        var surfaceBottom = -0.02f * tile;
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
