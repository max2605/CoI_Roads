using System;
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
using Mafi.Core.Entities.Validators;
using Mafi.Core.Input;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Simulation;
using Mafi.Localization;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Library;
using UnityEngine.EventSystems;

namespace GroundRoads;

/// <summary>
/// Places a multi-arm highway node and snaps any of its ports to an open port
/// of an existing highway segment. Nodes deliberately do not snap directly to
/// other nodes because the native traffic model needs a buffer segment between
/// adjacent conflict areas. A match is accepted only when both directed lane
/// graph keys are byte-identical.
/// </summary>
public abstract class HighwayNodePlacementControllerBase :
    IUnityInputController,
    IHotReloadUi
{
    private const int SnapSearchRange = 18;

    private static readonly Rotation90[] s_rotations =
    {
        Rotation90.Deg0,
        Rotation90.Deg90,
        Rotation90.Deg180,
        Rotation90.Deg270
    };

    private readonly IInputScheduler m_inputScheduler;
    private readonly IUnityInputMgr m_inputManager;
    private readonly CursorMessage m_cursorMessage;
    private readonly ShortcutsManager m_shortcuts;
    private readonly TerrainCursor m_terrainCursor;
    private readonly LayoutEntityPreviewManager m_previewManager;
    private readonly ConfigSerializationContext m_configContext;
    private readonly IEntitiesManager m_entitiesManager;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Lyst<HighwayPort> m_openPorts = new();
    private readonly Lyst<Tile2i> m_existingJunctionCenters = new();
    private readonly List<HighwayJunctionProto> m_protos;
    private readonly HighwayJunctionProto m_defaultProto;

    private LayoutEntityPreview m_preview;
    private HighwayJunctionProto m_proto;
    private TileTransform m_transform;
    private int m_preferredDirectionIndex;
    private bool m_isActive;
    private bool m_ignoreActivationClick;
    private bool m_isSnapped;
    private bool m_hasCursorError;

    public ControllerConfig Config => ControllerConfig.Tool;

    public bool IsActive => m_isActive;

    public HighwayJunctionProto Prototype => m_defaultProto;

    protected HighwayNodePlacementControllerBase(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        NewInstanceOf<CursorMessage> cursorMessage,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb,
        StaticEntityProto.ID protoId)
    {
        m_inputScheduler = context.InputScheduler;
        m_inputManager = context.InputMgr;
        m_cursorMessage = cursorMessage.Instance;
        m_shortcuts = context.ShortcutsManager;
        m_terrainCursor = terrainCursor.Instance;
        m_previewManager = previewManager;
        m_configContext = configContext;
        m_entitiesManager = entitiesManager;
        m_simLoopEvents = simLoopEvents;
        m_defaultProto =
            protosDb.GetOrThrow<HighwayJunctionProto>(protoId);
        m_protos = protosDb.All<HighwayJunctionProto>()
            .Where(x => x.Kind == m_defaultProto.Kind)
            .OrderBy(x => x.BaseDirectionIndex)
            .ToList();
        if (m_protos.Count != 4 ||
            m_protos.Where(
                (proto, index) => proto.BaseDirectionIndex != index).Any())
        {
            throw new InvalidOperationException(
                $"GroundRoads: expected four ordered variants for " +
                $"{m_defaultProto.Kind}.");
        }

        m_proto = m_defaultProto;
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
        ClearPlacementError();
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

        if (m_shortcuts.IsUp(m_shortcuts.Rotate))
        {
            m_preferredDirectionIndex =
                (m_preferredDirectionIndex + 1) & 15;
            return true;
        }

        if (!m_terrainCursor.HasValue)
        {
            ClearPlacementError();
            ClearPreview();
            return false;
        }

        UpdatePreview(m_terrainCursor.Tile3i);
        if (m_shortcuts.IsPrimaryActionUp && m_preview != null)
        {
            if (TryGetJunctionSpacingConflict(
                    out var proposedCenter,
                    out var existingCenter))
            {
                ShowPlacementError(
                    HighwayJunctionPlacementValidator.CreateSpacingError(
                        proposedCenter,
                        existingCenter));
                return true;
            }

            if (!m_preview.ValidationResult.HasValue)
            {
                return true;
            }

            var validation = m_preview.ValidationResult.Value;
            if (!validation.IsSuccess)
            {
                ShowPlacementError(validation);
                return true;
            }

            ClearPlacementError();
            var config = new EntityConfigData(m_proto, m_configContext)
            {
                Transform = m_transform
            };
            m_inputScheduler.ScheduleInputCmd(
                new BatchCreateStaticEntitiesCmd(
                    ImmutableArray.Create(config),
                    BuildMiniZippersMode.Never,
                    isFree: false,
                    allowValidationSuppression: false,
                    applyConfiguration: false));
            Log.Info(
                $"GroundRoads: scheduled {m_proto.Kind} at " +
                $"{m_transform.Position} (snapped: {m_isSnapped}).");
            return true;
        }

        return false;
    }

    private void SyncUpdate()
    {
        if (!m_isActive || !m_terrainCursor.HasValue)
        {
            m_openPorts.Clear();
            m_existingJunctionCenters.Clear();
            return;
        }

        HighwayPortDiscovery.FillOpenPortsNear(
            m_entitiesManager,
            m_terrainCursor.Tile3i.Xy,
            SnapSearchRange,
            includeJunctionPorts: false,
            result: m_openPorts);
        HighwayJunctionPlacementValidator.FillExistingCenters(
            m_entitiesManager,
            m_existingJunctionCenters);
    }

    private void UpdatePreview(Tile3i cursor)
    {
        m_isSnapped = TryCreateBestSnappedTransform(
            cursor,
            out var selectedProto,
            out m_transform);
        if (!m_isSnapped)
        {
            m_proto = m_protos[m_preferredDirectionIndex & 3];
            var rotation = s_rotations[
                (m_preferredDirectionIndex >> 2) & 3];
            m_transform = new TileTransform(
                cursor,
                rotation,
                isReflected: false);
        }
        else
        {
            m_proto = selectedProto;
        }

        if (m_preview != null &&
            m_preview.EntityProto.Id != m_proto.Id)
        {
            ClearPreview();
        }

        if (m_preview == null)
        {
            m_preview = m_previewManager.CreatePreview(
                m_proto,
                EntityPlacementPhase.FirstAndFinal,
                m_transform,
                disableValidation: false,
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

        if (m_hasCursorError &&
            !TryGetJunctionSpacingConflict(out _, out _) &&
            m_preview.ValidationResult.HasValue &&
            m_preview.ValidationResult.Value.IsSuccess)
        {
            ClearPlacementError();
        }
    }

    private bool TryGetJunctionSpacingConflict(
        out Tile2i proposedCenter,
        out Tile2i existingCenter)
    {
        proposedCenter = HighwayJunctionPlacementValidator.GetCenter(
            m_proto,
            m_transform);
        foreach (var center in m_existingJunctionCenters)
        {
            if (HighwayJunctionPlacementValidator.AreCentersTooClose(
                    proposedCenter,
                    center))
            {
                existingCenter = center;
                return true;
            }
        }

        existingCenter = default;
        return false;
    }

    private void ShowPlacementError(EntityValidationResult validation)
    {
        m_cursorMessage.MessageError(validation.ErrorMessageForPlayer);
        m_hasCursorError = true;
    }

    private void ClearPlacementError()
    {
        if (!m_hasCursorError)
        {
            return;
        }

        m_cursorMessage.MessageInfo(LocStrFormatted.Empty);
        m_hasCursorError = false;
    }

    private bool TryCreateBestSnappedTransform(
        Tile3i cursor,
        out HighwayJunctionProto selectedProto,
        out TileTransform result)
    {
        var found = false;
        var bestDistance = long.MaxValue;
        var bestRotationPenalty = int.MaxValue;
        selectedProto = m_defaultProto;
        result = default;

        foreach (var target in m_openPorts)
        {
            foreach (var proto in m_protos)
            {
                for (var rotationIndex = 0;
                     rotationIndex < s_rotations.Length;
                     rotationIndex++)
                {
                    var rotation = s_rotations[rotationIndex];
                    var orientation = new TileTransform(
                        Tile3i.Zero,
                        rotation,
                        isReflected: false);
                    for (var portIndex = 0;
                         portIndex < proto.HighwayPortCount;
                         portIndex++)
                    {
                        var relativePort =
                            proto.GetHighwayPort(portIndex, orientation);
                        var translation = new Tile3i(
                            target.Center.X - relativePort.Center.X,
                            target.Center.Y - relativePort.Center.Y,
                            target.Center.Z - relativePort.Center.Z);
                        var candidate = new TileTransform(
                            translation,
                            rotation,
                            isReflected: false);
                        var candidatePort =
                            proto.GetHighwayPort(portIndex, candidate);
                        if (!candidatePort.IsExactMateOf(target))
                        {
                            continue;
                        }

                        var distance =
                            candidate.Position.Xy.DistanceSqrTo(cursor.Xy);
                        var directionIndex =
                            (proto.BaseDirectionIndex +
                             rotationIndex * 4) & 15;
                        var clockwiseDelta =
                            (directionIndex -
                             m_preferredDirectionIndex + 16) & 15;
                        var counterClockwiseDelta =
                            (m_preferredDirectionIndex -
                             directionIndex + 16) & 15;
                        var rotationPenalty = Math.Min(
                            clockwiseDelta,
                            counterClockwiseDelta);
                        if (!found || distance < bestDistance ||
                            (distance == bestDistance &&
                             rotationPenalty < bestRotationPenalty))
                        {
                            found = true;
                            bestDistance = distance;
                            bestRotationPenalty = rotationPenalty;
                            selectedProto = proto;
                            result = candidate;
                        }
                    }
                }
            }
        }

        return found;
    }

    private void ClearPreview()
    {
        if (m_preview == null)
        {
            return;
        }

        m_preview.DestroyAndReturnToPool();
        m_preview = null;
    }

    public void DisposeForHotReload()
    {
        Deactivate();
        m_simLoopEvents.Sync.RemoveNonSaveable(this, SyncUpdate);
    }
}

public sealed class HighwayTIntersectionPlacementController :
    HighwayNodePlacementControllerBase
{
    public HighwayTIntersectionPlacementController(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        NewInstanceOf<CursorMessage> cursorMessage,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb)
        : base(
            context,
            terrainCursor,
            cursorMessage,
            previewManager,
            configContext,
            entitiesManager,
            simLoopEvents,
            protosDb,
            GroundRoadIds.HighwayTIntersection)
    {
    }
}

public sealed class HighwayCrossIntersectionPlacementController :
    HighwayNodePlacementControllerBase
{
    public HighwayCrossIntersectionPlacementController(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        NewInstanceOf<CursorMessage> cursorMessage,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb)
        : base(
            context,
            terrainCursor,
            cursorMessage,
            previewManager,
            configContext,
            entitiesManager,
            simLoopEvents,
            protosDb,
            GroundRoadIds.HighwayCrossIntersection)
    {
    }
}

public sealed class HighwayRoundaboutPlacementController :
    HighwayNodePlacementControllerBase
{
    public HighwayRoundaboutPlacementController(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        NewInstanceOf<CursorMessage> cursorMessage,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb)
        : base(
            context,
            terrainCursor,
            cursorMessage,
            previewManager,
            configContext,
            entitiesManager,
            simLoopEvents,
            protosDb,
            GroundRoadIds.HighwayRoundabout)
    {
    }
}
