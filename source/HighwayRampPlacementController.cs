using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Commands;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Input;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Simulation;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ui;
using UnityEngine.EventSystems;

namespace GroundRoads;

/// <summary>
/// Places one-way highway ramps. The preview follows the mouse and snaps the
/// ramp's centre and driven lane to an open end of a highway piece.
/// </summary>
public abstract class HighwayRampPlacementControllerBase :
    IUnityInputController,
    IHotReloadUi
{
    private const int SnapSearchRange = 18;

    private readonly IInputScheduler m_inputScheduler;
    private readonly IUnityInputMgr m_inputManager;
    private readonly ShortcutsManager m_shortcuts;
    private readonly TerrainCursor m_terrainCursor;
    private readonly LayoutEntityPreviewManager m_previewManager;
    private readonly ConfigSerializationContext m_configContext;
    private readonly IEntitiesManager m_entitiesManager;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Lyst<RoadConnectionPoint> m_highwayEnds = new();
    private readonly HashSet<RoadGraphNodeKey> m_occupiedRampNodes = new();
    private readonly List<GroundRoadEntranceProto> m_protos;
    private readonly GroundRoadEntranceProto m_defaultProto;
    private readonly bool m_isOnRamp;

    private LayoutEntityPreview m_preview;
    private GroundRoadEntranceProto m_proto;
    private TileTransform m_transform;
    private bool m_isActive;
    private bool m_ignoreActivationClick;
    private bool m_isSnapped;
    private bool m_previewValidationDisabled;

    public ControllerConfig Config => ControllerConfig.Tool;

    public bool IsActive => m_isActive;

    protected HighwayRampPlacementControllerBase(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        IEnumerable<GroundRoadEntranceProto> protos,
        StaticEntityProto.ID defaultProtoId,
        bool isOnRamp)
    {
        m_inputScheduler = context.InputScheduler;
        m_inputManager = context.InputMgr;
        m_shortcuts = context.ShortcutsManager;
        m_terrainCursor = terrainCursor.Instance;
        m_previewManager = previewManager;
        m_configContext = configContext;
        m_entitiesManager = entitiesManager;
        m_simLoopEvents = simLoopEvents;
        // Only V3 ramps participate in placement. Legacy V2 prototypes stay
        // in the database solely so saves containing them remain loadable.
        m_protos = protos
            .Where(x => x.FixedHighwayDirection.HasValue)
            .OrderBy(x => x.Id.Value)
            .ToList();
        m_defaultProto = m_protos.Single(x => x.Id == defaultProtoId);
        m_proto = m_defaultProto;
        m_isOnRamp = isOnRamp;
        m_simLoopEvents.Sync.AddNonSaveable(this, SyncUpdate);
    }

    public void Activate()
    {
        if (m_isActive)
        {
            return;
        }

        m_isActive = true;
        m_ignoreActivationClick = true;
        m_isSnapped = false;
        m_proto = m_defaultProto;
        m_terrainCursor.Activate();
    }

    public void Deactivate()
    {
        if (!m_isActive)
        {
            return;
        }

        m_isActive = false;
        m_isSnapped = false;
        ClearPreview();
        m_terrainCursor.Deactivate();
    }

    public bool InputUpdate()
    {
        if (m_shortcuts.IsPrimaryActionUp &&
            EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject())
        {
            return false;
        }

        if (m_ignoreActivationClick)
        {
            if (!m_shortcuts.IsPrimaryActionOn &&
                !m_shortcuts.IsPrimaryActionDown &&
                !m_shortcuts.IsPrimaryActionUp)
            {
                m_ignoreActivationClick = false;
            }

            return true;
        }

        if (m_shortcuts.IsSecondaryActionUp)
        {
            m_inputManager.DeactivateController(this);
            Deactivate();
            return true;
        }

        if (!m_terrainCursor.HasValue)
        {
            ClearPreview();
            return false;
        }

        UpdatePreview(m_terrainCursor.Tile3i);
        if (m_shortcuts.IsPrimaryActionUp &&
            m_isSnapped &&
            m_preview != null)
        {
            var config = new EntityConfigData(m_proto, m_configContext)
            {
                Transform = m_transform
            };
            var configs = ImmutableArray.Create(config);
            m_inputScheduler.ScheduleInputCmd(
                new BatchCreateStaticEntitiesCmd(
                    configs,
                    BuildMiniZippersMode.Never,
                    isFree: false,
                    // The ramp deliberately overlaps the visible shoulder of
                    // the highway at its exact graph node. Generic layout
                    // validation cannot distinguish that merge from an
                    // accidental building overlap, so use the same bounded
                    // suppression as the train-planned highway batch.
                    allowValidationSuppression: true,
                    applyConfiguration: false));
            Log.Info(
                $"GroundRoads: scheduled " +
                $"{(m_isOnRamp ? "on-ramp" : "off-ramp")} '{m_proto.Id}' " +
                $"at {m_transform.Position}.");
            return true;
        }

        return false;
    }

    private void SyncUpdate()
    {
        if (m_isActive && m_terrainCursor.HasValue)
        {
            m_highwayEnds.Clear();
            m_occupiedRampNodes.Clear();
            var cursor = m_terrainCursor.Tile3i.Xy;

            foreach (var ramp in
                     m_entitiesManager.GetAllEntitiesOfType<RoadEntranceEntity>())
            {
                switch (ramp.Prototype)
                {
                    case HighwayOnRampProto onRamp:
                        m_occupiedRampNodes.Add(
                            onRamp.GetTransformedEndGraphNode(
                                0,
                                ramp.Transform));
                        break;
                    case HighwayOffRampProto offRamp:
                        m_occupiedRampNodes.Add(
                            offRamp.GetTransformedStartGraphNode(
                                0,
                                ramp.Transform));
                        break;
                }
            }

            foreach (var road in
                     m_entitiesManager.GetAllEntitiesOfType<RoadEntityBase>())
            {
                if (road.RoadProto is not HighwaySegmentProto highway)
                {
                    continue;
                }

                var start = new RoadConnectionPoint(
                    road,
                    highway,
                    atStart: true);
                if (start.GetNode().Xy.IsNear(cursor, SnapSearchRange) &&
                    !m_occupiedRampNodes.Contains(
                        GetTargetLaneNode(highway, start)))
                {
                    m_highwayEnds.Add(start);
                }

                var end = new RoadConnectionPoint(
                    road,
                    highway,
                    atStart: false);
                if (end.GetNode().Xy.IsNear(cursor, SnapSearchRange) &&
                    !m_occupiedRampNodes.Contains(
                        GetTargetLaneNode(highway, end)))
                {
                    m_highwayEnds.Add(end);
                }
            }
        }
        else
        {
            m_highwayEnds.Clear();
        }
    }

    private void UpdatePreview(Tile3i cursor)
    {
        m_isSnapped = TryCreateBestSnappedTransform(
            cursor,
            out m_proto,
            out m_transform);

        if (!m_isSnapped)
        {
            m_proto = m_defaultProto;
            m_transform = m_proto.GetTransformThatPositionsTerrainAt(
                cursor,
                Rotation90.Deg0,
                isReflected: false);
        }

        if (m_preview != null &&
            (m_preview.EntityProto.Id != m_proto.Id ||
             m_previewValidationDisabled != m_isSnapped))
        {
            ClearPreview();
        }

        if (m_preview == null)
        {
            m_previewValidationDisabled = m_isSnapped;
            m_preview = m_previewManager.CreatePreview(
                m_proto,
                EntityPlacementPhase.FirstAndFinal,
                m_transform,
                // We validate the target graph node exactly in
                // TryCreateSnappedTransform. Layout validation would reject
                // the intentional shoulder overlap before that graph-level
                // check gets a chance to build the merge.
                disableValidation: m_isSnapped,
                disablePortPreviews: false,
                enableMiniZipperPlacement: false,
                isForRemoval: false,
                disablePortPreviewsPredicate: null,
                disableHighlight: false);
        }
        else
        {
            m_preview.SetTransform(m_transform);
        }
    }

    private bool TryCreateBestSnappedTransform(
        Tile3i cursor,
        out GroundRoadEntranceProto selectedProto,
        out TileTransform result)
    {
        var found = false;
        var bestDistance = long.MaxValue;
        var visitedTargets = new HashSet<RoadGraphNodeKey>();
        selectedProto = m_defaultProto;
        result = default;

        // Internal train-piece seams expose two coincident centre points but
        // different directed lane nodes. Evaluate all of those nodes before
        // choosing a candidate. Selecting the centre point first was the
        // source of the seemingly random 180-degree ramp reversal.
        foreach (var target in m_highwayEnds)
        {
            var highway = (HighwaySegmentProto)target.Proto;
            var targetLaneNode = GetTargetLaneNode(highway, target);
            if (!visitedTargets.Add(targetLaneNode) ||
                m_occupiedRampNodes.Contains(targetLaneNode))
            {
                continue;
            }

            foreach (var proto in m_protos)
            {
                // Each V3 prototype already owns one exact world-space
                // train heading. Translation is the only legal transform;
                // rotating or reflecting it would decouple mesh direction
                // from the directed graph edge.
                var orientation = new TileTransform(
                    Tile3i.Zero,
                    Rotation90.Deg0,
                    isReflected: false);
                var relativeNode = m_isOnRamp
                    ? proto.GetTransformedEndGraphNode(0, orientation)
                    : proto.GetTransformedStartGraphNode(0, orientation);
                var translationRel =
                    targetLaneNode.Position - relativeNode.Position;
                var translation = new Tile3i(
                    translationRel.X.ToIntRounded(),
                    translationRel.Y.ToIntRounded(),
                    translationRel.Z.ToIntRounded());
                var candidate = new TileTransform(
                    translation,
                    Rotation90.Deg0,
                    isReflected: false);
                var rampNode = m_isOnRamp
                    ? proto.GetTransformedEndGraphNode(0, candidate)
                    : proto.GetTransformedStartGraphNode(0, candidate);
                if (rampNode != targetLaneNode)
                {
                    continue;
                }

                var terrainPoint =
                    proto.GetTransformedTerrainPoint(candidate);
                var distance = terrainPoint.Xy.DistanceSqrTo(cursor.Xy);
                if (!found || distance < bestDistance)
                {
                    found = true;
                    bestDistance = distance;
                    selectedProto = proto;
                    result = candidate;
                }
            }
        }

        return found;
    }

    private RoadGraphNodeKey GetTargetLaneNode(
        HighwaySegmentProto highway,
        RoadConnectionPoint target)
    {
        if (m_isOnRamp)
        {
            return target.AtStart
                ? highway.GetTransformedStartGraphNode(
                    0,
                    target.Entity.Transform)
                : highway.GetTransformedStartGraphNode(
                    1,
                    target.Entity.Transform);
        }

        return target.AtStart
            ? highway.GetTransformedEndGraphNode(
                1,
                target.Entity.Transform)
            : highway.GetTransformedEndGraphNode(
                0,
                target.Entity.Transform);
    }

    private void ClearPreview()
    {
        if (m_preview == null)
        {
            return;
        }

        m_preview.DestroyAndReturnToPool();
        m_preview = null;
        m_previewValidationDisabled = false;
    }

    public void DisposeForHotReload()
    {
        Deactivate();
        m_simLoopEvents.Sync.RemoveNonSaveable(this, SyncUpdate);
    }
}

public sealed class HighwayOnRampPlacementController :
    HighwayRampPlacementControllerBase
{
    public HighwayOnRampPlacementController(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb)
        : base(
            context,
            terrainCursor,
            previewManager,
            configContext,
            entitiesManager,
            simLoopEvents,
            protosDb.All<HighwayOnRampProto>(),
            GroundRoadIds.HighwayOnRampV3,
            isOnRamp: true)
    {
    }
}

public sealed class HighwayOffRampPlacementController :
    HighwayRampPlacementControllerBase
{
    public HighwayOffRampPlacementController(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb)
        : base(
            context,
            terrainCursor,
            previewManager,
            configContext,
            entitiesManager,
            simLoopEvents,
            protosDb.All<HighwayOffRampProto>(),
            GroundRoadIds.HighwayOffRampV3,
            isOnRamp: false)
    {
    }
}
