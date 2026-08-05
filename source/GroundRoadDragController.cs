using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static.Commands;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Input;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.Simulation;
using Mafi.Core.Trains;
using Mafi.Localization;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.Ui.Hud.Toolbar.MenuItems;
using UnityEngine;
using UnityEngine.EventSystems;

namespace GroundRoads;

/// <summary>
/// Highway construction tool backed by the public train-track path finder.
/// It uses the same discrete curve/radius library as the Trains DLC, but maps
/// every plan step to a native road entity before the build command is sent.
/// </summary>
public sealed class GroundRoadDragController :
    IUnityInputController,
    IHotReloadUi
{
    private const int MinimumPathFindingIterationsPerFrame = 12500;
    private const int MaximumPathFindingIterationsPerFrame = 100000;
    private const int IterationsPerLockedPiece = 64;
    private const int MaximumPreviewPieces = 1200;
    private const float DoubleClickWindowSeconds = 0.30f;
    private const float MaximumSearchSecondsPerGoal = 8f;
    private const float PointerClickMaxDistanceSquared = 64f;
    private const int PortSnapSearchRange = 12;
    private const int PortSnapMaxDistanceSquared = 9;

    private readonly struct Piece
    {
        public readonly HighwaySegmentProto Proto;
        public readonly TileTransform Transform;
        public readonly TrainTrackTrajectoryDirection TrackDirection;

        public Piece(
            HighwaySegmentProto proto,
            TileTransform transform,
            TrainTrackTrajectoryDirection trackDirection)
        {
            Proto = proto;
            Transform = transform;
            TrackDirection = trackDirection;
        }
    }

    private readonly IInputScheduler m_inputScheduler;
    private readonly IUnityInputMgr m_inputManager;
    private readonly ShortcutsManager m_shortcuts;
    private readonly TerrainCursor m_terrainCursor;
    private readonly LayoutEntityPreviewManager m_previewManager;
    private readonly ConfigSerializationContext m_configContext;
    private readonly ITrainTrackPathFinder m_trackPathFinder;
    private readonly IEntitiesManager m_entitiesManager;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Dictionary<(string TrackId, bool Reflected),
        HighwaySegmentProto> m_segmentsByTrackId;
    private readonly List<Piece> m_pieces = new();
    private readonly List<LayoutEntityPreview> m_previews = new();
    private readonly Lyst<HighwayPort> m_openPorts = new();

    private bool m_isActive;
    private bool m_hasStart;
    private bool m_searchInitialized;
    private bool m_searchInProgress;
    private bool m_previewDirty;
    private bool m_currentPlanIsExact;
    private bool m_automaticStartDirection;
    private bool m_ignoreActivationClick;
    private bool m_hasConfirmationClick;
    private bool m_hasPendingPrimaryClick;
    private bool m_pendingPrimaryWantsConfirmation;
    private bool m_pendingPrimaryContinueAfterBuild;
    private bool m_primaryPressTracking;
    private bool m_secondaryPressTracking;
    private bool m_continueAfterPendingBuild;
    private Tile3f m_anchor;
    private Tile3f m_searchGoal;
    private Tile3f m_lastConfirmationEndpoint;
    private Tile3f m_pendingPrimaryGoal;
    private HighwayPort? m_searchGoalPort;
    private HighwayPort? m_pendingPrimaryGoalPort;
    private Option<TrainTrackPlan> m_lockedPlan;
    private Option<TrainTrackPlan> m_currentPlan;
    private TrainTrackNodeDirection? m_forcedStartDirection;
    private TrainTrackNodeDirection? m_forcedEndDirection;
    private TrainTrackNodeDirection? m_searchEndDirection;
    private TrainTrackNodeDirection? m_pendingPrimaryEndDirection;
    private TrainTrackGraphNodeKey? m_continuationStartNode;
    private Piece? m_continuationPredecessor;
    private BatchCreateStaticEntitiesCmd m_pendingBuildCommand;
    private TrainTrackPlan m_pendingContinuationPlan;
    private TrainTrackGraphNodeKey m_pendingContinuationNode;
    private Piece m_pendingContinuationLastPiece;
    private int m_builtPrefixStepCount;
    private int m_directionIndex;
    private int m_lastConfirmationStepCount;
    private float m_lastConfirmationClickTime;
    private float m_pendingPrimaryClickTime;
    private float m_searchGoalStartedAt;
    private Vector3 m_lastConfirmationScreenPosition;
    private Vector3 m_pendingPrimaryScreenPosition;
    private Vector3 m_primaryPressScreenPosition;
    private Vector3 m_secondaryPressScreenPosition;

    public ControllerConfig Config => ControllerConfig.Tool;

    public bool IsActive => m_isActive;

    public GroundRoadDragController(
        UiContext context,
        NewInstanceOf<TerrainCursor> terrainCursor,
        LayoutEntityPreviewManager previewManager,
        ConfigSerializationContext configContext,
        ITrainTrackPathFinder trackPathFinder,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb)
    {
        m_inputScheduler = context.InputScheduler;
        m_inputManager = context.InputMgr;
        m_shortcuts = context.ShortcutsManager;
        m_terrainCursor = terrainCursor.Instance;
        m_previewManager = previewManager;
        m_configContext = configContext;
        m_trackPathFinder = trackPathFinder;
        m_entitiesManager = entitiesManager;
        m_simLoopEvents = simLoopEvents;
        m_segmentsByTrackId = protosDb.All<HighwaySegmentProto>()
            .ToDictionary(
                x => (
                    x.SourceTrackProto.Id.Value,
                    x.CorrectsReflectedHandedness),
                x => x);
        m_simLoopEvents.Sync.AddNonSaveable(this, SyncUpdate);
    }

    public void Activate()
    {
        if (m_isActive)
        {
            return;
        }

        m_isActive = true;
        // The toolbar itself is activated with Mouse0. Without a release
        // guard that same UI click becomes the first terrain anchor before
        // the pointer has even returned to the world.
        m_ignoreActivationClick = true;
        if (m_pendingBuildCommand == null)
        {
            ResetPlan();
        }
        else
        {
            m_continueAfterPendingBuild = false;
            ResetPlanningState();
        }
        m_terrainCursor.Activate();
        Log.Info("GroundRoads: train-planned highway tool activated.");
    }

    public void Deactivate()
    {
        if (!m_isActive)
        {
            return;
        }

        m_isActive = false;
        // A scheduled command cannot be unscheduled. Keep observing it if
        // the tool is activated again, but never auto-continue after the
        // user has left the tool.
        if (m_pendingBuildCommand == null)
        {
            ResetPlan();
        }
        else
        {
            m_continueAfterPendingBuild = false;
            ResetPlanningState();
        }
        m_terrainCursor.Deactivate();
    }

    public bool InputUpdate()
    {
        // Match the native build controllers: mouse actions over UI never
        // become terrain pivots, confirmations, or cancellations.
        var pointerOverUi = EventSystem.current != null &&
                            EventSystem.current.IsPointerOverGameObject();
        if (pointerOverUi &&
            (m_shortcuts.IsPrimaryActionDown ||
             m_shortcuts.IsPrimaryActionUp ||
             m_shortcuts.IsSecondaryActionDown ||
             m_shortcuts.IsSecondaryActionUp))
        {
            m_primaryPressTracking = false;
            m_secondaryPressTracking = false;
            return false;
        }

        if (UpdatePendingBuild())
        {
            return true;
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

        if (m_shortcuts.IsPrimaryActionDown)
        {
            m_primaryPressTracking = true;
            m_primaryPressScreenPosition = Input.mousePosition;
        }

        var isPrimaryClickRelease = false;
        if (m_shortcuts.IsPrimaryActionUp)
        {
            isPrimaryClickRelease = m_primaryPressTracking &&
                (Input.mousePosition - m_primaryPressScreenPosition)
                .sqrMagnitude <= PointerClickMaxDistanceSquared;
            m_primaryPressTracking = false;
        }

        // A right-button drag belongs to the camera. Only a real click is a
        // cancellation, which avoids destroying a long plan while panning.
        if (m_shortcuts.IsSecondaryActionDown)
        {
            m_secondaryPressTracking = true;
            m_secondaryPressScreenPosition = Input.mousePosition;
            return false;
        }

        if (m_shortcuts.IsSecondaryActionUp)
        {
            var wasClick = m_secondaryPressTracking &&
                (Input.mousePosition - m_secondaryPressScreenPosition)
                .sqrMagnitude <= PointerClickMaxDistanceSquared;
            m_secondaryPressTracking = false;
            if (!wasClick)
            {
                return false;
            }

            InvalidateConfirmationClick();
            if (m_hasStart)
            {
                ResetPlanningState();
            }
            else
            {
                m_inputManager.DeactivateController(this);
                Deactivate();
            }

            return true;
        }

        if (m_shortcuts.IsUp(m_shortcuts.Rotate))
        {
            if (!m_hasStart)
            {
                return true;
            }

            // Once a pivot is locked, its outgoing tangent is a hard seam
            // contract. Rotating it would create the angular break this tool
            // is specifically designed to prevent.
            if (m_lockedPlan.HasValue)
            {
                return true;
            }

            m_directionIndex = (m_directionIndex + 1) & 15;
            TrainTrackNodeDirection.TryCreateFromAngle(
                (m_directionIndex * 22.5).Degrees(),
                TrainTrackGradeFactor.G0,
                out var forced,
                out _);
            m_forcedStartDirection = forced;
            m_automaticStartDirection = false;
            InvalidateConfirmationClick();
            InvalidatePendingPrimaryClick();
            RestartSearch();
            return true;
        }

        if (m_shortcuts.IsUp(m_shortcuts.Flip) &&
            m_forcedStartDirection.HasValue)
        {
            if (m_lockedPlan.HasValue)
            {
                return true;
            }

            m_forcedStartDirection =
                m_forcedStartDirection.Value.Inversed();
            m_directionIndex = GetDirectionIndex(
                m_forcedStartDirection.Value);
            m_automaticStartDirection = false;
            InvalidateConfirmationClick();
            InvalidatePendingPrimaryClick();
            RestartSearch();
            return true;
        }

        if (!m_terrainCursor.HasValue)
        {
            ClearPreviews();
            m_previewDirty = true;
            return false;
        }

        if (!m_hasStart)
        {
            if (isPrimaryClickRelease)
            {
                if (TryGetClosestOpenPort(
                        m_terrainCursor.Tile3i,
                        out var snappedPort))
                {
                    m_anchor = snappedPort.Center.CornerTile3f;
                    m_forcedStartDirection =
                        snappedPort.OutboundNode.Direction;
                    m_directionIndex = GetDirectionIndex(
                        m_forcedStartDirection.Value);
                    m_automaticStartDirection = false;
                }
                else
                {
                    m_anchor = m_trackPathFinder.GetStartFromTile(
                        m_terrainCursor.Tile3f);
                    m_automaticStartDirection = true;
                    m_forcedStartDirection = null;
                }
                m_hasStart = true;
                m_previewDirty = true;
                m_lockedPlan = Option<TrainTrackPlan>.None;
                m_currentPlan = Option<TrainTrackPlan>.None;
                m_currentPlanIsExact = false;
                m_forcedEndDirection = null;
                m_searchEndDirection = null;
                m_searchGoalPort = null;
                m_searchInitialized = false;
                m_searchInProgress = false;
                m_searchGoalStartedAt = Time.unscaledTime;
                InvalidateConfirmationClick();
                InvalidatePendingPrimaryClick();
                return true;
            }

            return false;
        }

        UpdatePathSearch();
        TryResolvePendingPrimaryClick(out var pendingBuildScheduled);
        if (pendingBuildScheduled)
        {
            return true;
        }

        if (isPrimaryClickRelease)
        {
            if (IsConfirmationDoubleClick())
            {
                var continueAfterBuild =
                    m_shortcuts.IsOn(m_shortcuts.PlaceMultiple);
                if (m_lockedPlan.HasValue)
                {
                    if (!TryScheduleBuild(
                            m_lockedPlan.Value,
                            continueAfterBuild))
                    {
                        RecordConfirmationClick(
                            Time.unscaledTime,
                            Input.mousePosition);
                    }
                }

                return true;
            }

            if (!TryLockCurrentPlan(
                    Time.unscaledTime,
                    Input.mousePosition))
            {
                if (m_lockedPlan.HasValue &&
                    m_searchGoal == m_anchor)
                {
                    RecordConfirmationClick(
                        Time.unscaledTime,
                        Input.mousePosition);
                }
                else
                {
                    QueuePendingPrimaryClick();
                }
            }

            return true;
        }

        return false;
    }

    public void DisposeForHotReload()
    {
        Deactivate();
        m_simLoopEvents.Sync.RemoveNonSaveable(this, SyncUpdate);
    }

    private void SyncUpdate()
    {
        if (!m_isActive || !m_terrainCursor.HasValue)
        {
            m_openPorts.Clear();
            return;
        }

        HighwayPortDiscovery.FillOpenPortsNear(
            m_entitiesManager,
            m_terrainCursor.Tile3i.Xy,
            PortSnapSearchRange,
            includeJunctionPorts: true,
            result: m_openPorts);
    }

    private bool UpdatePendingBuild()
    {
        if (m_pendingBuildCommand == null)
        {
            return false;
        }

        if (m_shortcuts.IsSecondaryActionDown)
        {
            m_secondaryPressTracking = true;
            m_secondaryPressScreenPosition = Input.mousePosition;
            if (m_continueAfterPendingBuild)
            {
                m_continueAfterPendingBuild = false;
                Log.Info(
                    "GroundRoads: automatic continuation cancelled while " +
                    "the committed build was synchronizing.");
            }

            return true;
        }

        if (m_shortcuts.IsSecondaryActionUp)
        {
            m_secondaryPressTracking = false;
            return true;
        }

        if (!m_pendingBuildCommand.IsProcessedAndSynced)
        {
            return true;
        }

        var command = m_pendingBuildCommand;
        var continueAfterBuild = m_continueAfterPendingBuild;
        var continuationPlan = m_pendingContinuationPlan;
        var continuationNode = m_pendingContinuationNode;
        var continuationLastPiece = m_pendingContinuationLastPiece;
        m_pendingBuildCommand = null;
        m_continueAfterPendingBuild = false;

        var succeeded = command.ResultSet &&
                        command.Result &&
                        !command.HasError;
        if (!succeeded)
        {
            var reason = command.ResultSet && command.HasError
                ? command.ErrorMessage
                : "the build command returned no successful result";
            Log.Warning(
                $"GroundRoads: highway build failed: {reason}.");
            ResetPlanningState();
            return true;
        }

        if (continueAfterBuild && m_isActive)
        {
            BeginContinuation(
                continuationPlan,
                continuationNode,
                continuationLastPiece);
            Log.Info(
                "GroundRoads: continuing highway placement at the exact " +
                "committed end tangent.");
        }
        else
        {
            ResetPlanningState();
        }

        return true;
    }

    private void BeginContinuation(
        TrainTrackPlan parentPlan,
        TrainTrackGraphNodeKey startNode,
        Piece predecessor)
    {
        ResetPlanningState();
        m_hasStart = true;
        m_anchor = startNode.Position;
        m_searchGoal = startNode.Position;
        m_searchGoalStartedAt = Time.unscaledTime;
        m_builtPrefixStepCount = parentPlan.Steps.Length;
        m_lockedPlan = parentPlan;
        m_forcedStartDirection = startNode.Direction;
        m_directionIndex = GetDirectionIndex(startNode.Direction);
        m_automaticStartDirection = false;
        m_continuationStartNode = startNode;
        m_continuationPredecessor = predecessor;
        m_previewDirty = true;
    }

    private bool IsConfirmationDoubleClick()
    {
        if (!m_hasConfirmationClick || !m_lockedPlan.HasValue)
        {
            return false;
        }

        var elapsed = Time.unscaledTime - m_lastConfirmationClickTime;
        if (elapsed < 0f || elapsed > DoubleClickWindowSeconds)
        {
            InvalidateConfirmationClick();
            return false;
        }

        return m_anchor == m_lastConfirmationEndpoint &&
               m_searchGoal == m_lastConfirmationEndpoint &&
               m_lockedPlan.Value.Steps.Length ==
                   m_lastConfirmationStepCount &&
               (Input.mousePosition - m_lastConfirmationScreenPosition)
                   .sqrMagnitude <= PointerClickMaxDistanceSquared;
    }

    private bool TryLockCurrentPlan(
        float clickTime,
        Vector3 clickScreenPosition)
    {
        if (!m_currentPlan.HasValue ||
            !m_currentPlanIsExact ||
            !AllPreviewsValid() ||
            !DoesCurrentPlanMateSelectedGoalPort())
        {
            return false;
        }

        var plan = m_currentPlan.Value;
        if (!IsRealPlanExtension(plan) ||
            !TryGetExactPlanEndNode(plan, out var endNode))
        {
            return false;
        }

        m_lockedPlan = plan;
        m_anchor = endNode.Position;
        m_forcedStartDirection = endNode.Direction;
        m_directionIndex = GetDirectionIndex(endNode.Direction);
        m_automaticStartDirection = false;
        m_currentPlan = Option<TrainTrackPlan>.None;
        m_currentPlanIsExact = false;
        m_searchInitialized = false;
        m_searchInProgress = false;
        m_previewDirty = true;
        SynchronizePlan(plan);

        RecordConfirmationClick(clickTime, clickScreenPosition);
        return true;
    }

    private void RecordConfirmationClick(
        float clickTime,
        Vector3 clickScreenPosition)
    {
        if (!m_lockedPlan.HasValue)
        {
            return;
        }

        m_hasConfirmationClick = true;
        m_lastConfirmationClickTime = clickTime;
        m_lastConfirmationScreenPosition = clickScreenPosition;
        m_lastConfirmationEndpoint = m_anchor;
        m_lastConfirmationStepCount = m_lockedPlan.Value.Steps.Length;
    }

    private void QueuePendingPrimaryClick()
    {
        var clickTime = Time.unscaledTime;
        var clickPosition = Input.mousePosition;
        var clickedGoal = ResolveCursorGoal(
            out var endDirection,
            out var goalPort);
        if (m_hasPendingPrimaryClick &&
            m_pendingPrimaryGoal == clickedGoal &&
            m_pendingPrimaryEndDirection == endDirection &&
            AreSamePort(m_pendingPrimaryGoalPort, goalPort) &&
            clickTime - m_pendingPrimaryClickTime >= 0f &&
            clickTime - m_pendingPrimaryClickTime <=
                DoubleClickWindowSeconds &&
            (clickPosition - m_pendingPrimaryScreenPosition)
                .sqrMagnitude <= PointerClickMaxDistanceSquared)
        {
            m_pendingPrimaryWantsConfirmation = true;
            m_pendingPrimaryContinueAfterBuild =
                m_shortcuts.IsOn(m_shortcuts.PlaceMultiple);
            return;
        }

        m_hasPendingPrimaryClick = true;
        m_pendingPrimaryWantsConfirmation = false;
        m_pendingPrimaryContinueAfterBuild = false;
        m_pendingPrimaryGoal = clickedGoal;
        m_pendingPrimaryEndDirection = endDirection;
        m_pendingPrimaryGoalPort = goalPort;
        m_pendingPrimaryClickTime = clickTime;
        m_pendingPrimaryScreenPosition = clickPosition;
    }

    private bool TryResolvePendingPrimaryClick(
        out bool buildScheduled)
    {
        buildScheduled = false;
        if (!m_hasPendingPrimaryClick)
        {
            return false;
        }

        if (m_searchGoal != m_pendingPrimaryGoal ||
            m_searchEndDirection != m_pendingPrimaryEndDirection ||
            !AreSamePort(
                m_searchGoalPort,
                m_pendingPrimaryGoalPort))
        {
            InvalidatePendingPrimaryClick();
            return false;
        }

        var wantsConfirmation = m_pendingPrimaryWantsConfirmation;
        var continueAfterBuild = m_pendingPrimaryContinueAfterBuild;
        var clickTime = m_pendingPrimaryClickTime;
        var clickPosition = m_pendingPrimaryScreenPosition;
        if (!TryLockCurrentPlan(clickTime, clickPosition))
        {
            var validationFinishedAndFailed =
                m_currentPlan.HasValue &&
                m_currentPlanIsExact &&
                m_previews.Count > 0 &&
                m_previews.All(
                    preview => preview.ValidationResult.HasValue) &&
                !AllPreviewsValid();
            if (validationFinishedAndFailed)
            {
                InvalidatePendingPrimaryClick();
            }

            return false;
        }

        InvalidatePendingPrimaryClick();
        if (wantsConfirmation && m_lockedPlan.HasValue)
        {
            buildScheduled = TryScheduleBuild(
                m_lockedPlan.Value,
                continueAfterBuild);
            if (!buildScheduled)
            {
                RecordConfirmationClick(
                    Time.unscaledTime,
                    clickPosition);
            }
        }

        return true;
    }

    private bool IsRealPlanExtension(TrainTrackPlan plan)
    {
        if (plan.Steps.IsEmpty)
        {
            return false;
        }

        if (!m_lockedPlan.HasValue)
        {
            return true;
        }

        var lockedSteps = m_lockedPlan.Value.Steps;
        if (plan.Steps.Length <= lockedSteps.Length)
        {
            return false;
        }

        for (var index = 0; index < lockedSteps.Length; index++)
        {
            if (!plan.Steps[index].Equals(lockedSteps[index]))
            {
                Log.Error(
                    "GroundRoads: path finder returned a plan whose " +
                    "prefix differs from its locked pivot history.");
                return false;
            }
        }

        return true;
    }

    private static bool TryGetExactPlanEndNode(
        TrainTrackPlan plan,
        out TrainTrackGraphNodeKey endNode)
    {
        endNode = default;
        if (plan.Steps.IsEmpty || !plan.LastStepEndPosition.HasValue)
        {
            return false;
        }

        var lastStep = plan.Steps.Last;
        GetLogicalTrackNodes(lastStep, out _, out endNode);
        return endNode.Position == plan.LastStepEndPosition.Value &&
               endNode.Direction == lastStep.EndDirection;
    }

    private static void GetLogicalTrackNodes(
        TrainTrackPlanStep step,
        out TrainTrackGraphNodeKey startNode,
        out TrainTrackGraphNodeKey endNode)
    {
        step.Proto.GetTransformedGraphNodes(
            step.Transform,
            startBridge: false,
            endBridge: false,
            out var physicalStart,
            out var physicalEnd);
        if (step.TrackDirection ==
            TrainTrackTrajectoryDirection.Backward)
        {
            startNode = physicalEnd.Inversed();
            endNode = physicalStart.Inversed();
        }
        else
        {
            startNode = physicalStart;
            endNode = physicalEnd;
        }
    }

    private static int GetDirectionIndex(
        TrainTrackNodeDirection direction)
    {
        for (var index = 0; index < 16; index++)
        {
            TrainTrackNodeDirection.TryCreateFromAngle(
                (index * 22.5).Degrees(),
                TrainTrackGradeFactor.G0,
                out var candidate,
                out _);
            if (candidate == direction)
            {
                return index;
            }
        }

        return 0;
    }

    private void InvalidateConfirmationClick()
    {
        m_hasConfirmationClick = false;
        m_lastConfirmationStepCount = 0;
    }

    private void InvalidatePendingPrimaryClick()
    {
        m_hasPendingPrimaryClick = false;
        m_pendingPrimaryWantsConfirmation = false;
        m_pendingPrimaryContinueAfterBuild = false;
        m_pendingPrimaryEndDirection = null;
        m_pendingPrimaryGoalPort = null;
    }

    private void UpdatePathSearch()
    {
        // Highway prototypes are intentionally flat. TerrainCursor follows
        // the local terrain height, so project the target onto the anchor
        // plane or the train finder can only return an endless approximation.
        Tile3f directionGoal;
        Tile3f goal;
        HighwayPort? goalPort;
        TrainTrackNodeDirection? endDirection;
        if (m_hasPendingPrimaryClick)
        {
            goal = m_pendingPrimaryGoal;
            directionGoal = goal;
            endDirection = m_pendingPrimaryEndDirection;
            goalPort = m_pendingPrimaryGoalPort;
        }
        else
        {
            goal = ResolveCursorGoal(
                out endDirection,
                out goalPort);
            directionGoal = goal;
        }

        m_forcedEndDirection = endDirection;
        UpdateAutomaticStartDirection(directionGoal);
        var options = CreateOptions(endDirection);

        if (goal != m_searchGoal ||
            endDirection != m_searchEndDirection ||
            !AreSamePort(goalPort, m_searchGoalPort))
        {
            m_searchGoal = goal;
            m_searchEndDirection = endDirection;
            m_searchGoalPort = goalPort;
            RestartSearch();
        }

        if (m_hasPendingPrimaryClick &&
            Time.unscaledTime - m_pendingPrimaryClickTime >=
                MaximumSearchSecondsPerGoal)
        {
            InvalidatePendingPrimaryClick();
            Log.Warning(
                "GroundRoads: discarded a buffered placement click after " +
                "the preview could not become buildable in time.");
        }

        if (!m_previewDirty && !m_searchInProgress)
        {
            return;
        }

        var lockedStepCount = m_lockedPlan.HasValue
            ? m_lockedPlan.Value.Steps.Length
            : 0;
        var iterations = Math.Min(
            MaximumPathFindingIterationsPerFrame,
            Math.Max(
                MinimumPathFindingIterationsPerFrame,
                MinimumPathFindingIterationsPerFrame +
                lockedStepCount * IterationsPerLockedPiece));
        Option<TrainTrackPlan> result;
        bool needsMore;
        bool approximate;
        bool noRoute;

        if (!m_searchInitialized)
        {
            result = m_trackPathFinder.StartPathFinding(
                m_anchor,
                goal,
                options,
                m_lockedPlan,
                ref iterations,
                out needsMore,
                out approximate,
                out noRoute);
            m_searchInitialized = true;
        }
        else
        {
            result = m_trackPathFinder.ContinuePathFinding(
                goal,
                options,
                m_lockedPlan,
                ref iterations,
                out needsMore,
                out approximate,
                out noRoute);
        }

        m_searchInProgress = needsMore;
        m_previewDirty = needsMore;

        if (result.HasValue && !noRoute)
        {
            m_currentPlan = result;
            m_currentPlanIsExact = !approximate &&
                result.Value.LastStepEndPosition.HasValue &&
                result.Value.LastStepEndPosition.Value == goal;
            SynchronizePlan(result.Value);
        }
        else if (noRoute)
        {
            m_currentPlan = Option<TrainTrackPlan>.None;
            m_currentPlanIsExact = false;
            InvalidatePendingPrimaryClick();
            ShowLockedPlanOnly();
        }

        if (m_searchInProgress &&
            !m_currentPlanIsExact &&
            Time.unscaledTime - m_searchGoalStartedAt >=
                MaximumSearchSecondsPerGoal)
        {
            m_searchInProgress = false;
            m_previewDirty = false;
            InvalidatePendingPrimaryClick();
            Log.Warning(
                "GroundRoads: stopped an unresolved highway search after " +
                $"{MaximumSearchSecondsPerGoal:0} seconds. Move the target " +
                "or add a nearer pivot to retry.");
        }

    }

    private void UpdateAutomaticStartDirection(Tile3f rawGoal)
    {
        if (!m_automaticStartDirection || m_lockedPlan.HasValue)
        {
            return;
        }

        var delta = rawGoal - m_anchor;
        if (delta.X.IsZero && delta.Y.IsZero)
        {
            return;
        }

        if (!TrainTrackNodeDirection.TryCreateFromAngle(
                delta.Angle,
                TrainTrackGradeFactor.G0,
                out var direction,
                out _))
        {
            return;
        }

        if (m_forcedStartDirection.HasValue &&
            m_forcedStartDirection.Value == direction)
        {
            return;
        }

        m_forcedStartDirection = direction;
        m_directionIndex = GetDirectionIndex(direction);
        RestartSearch();
    }

    private TrainTrackPathFinderOptions CreateOptions(
        TrainTrackNodeDirection? forcedEndDirection)
    {
        // Prevent the native near-goal singularity mode from limiting a
        // route to one direction/radius change. This is what enables
        // repeated left/right bends and true S-curves.
        var flags =
            TrainTrackPathFinderFlags.GoalMustBeFlat |
            TrainTrackPathFinderFlags.DisallowG4 |
            TrainTrackPathFinderFlags.DisallowG8 |
            TrainTrackPathFinderFlags.AlternativeMode;

        return new TrainTrackPathFinderOptions(
            forcedStartDirectionA: m_forcedStartDirection,
            forcedEndDirectionA: forcedEndDirection,
            buildDirection: TrainTrackTrajectoryDirection.Bidirectional,
            flags: flags);
    }

    private Tile3f ResolveCursorGoal(
        out TrainTrackNodeDirection? endDirection,
        out HighwayPort? goalPort)
    {
        Tile3f rawGoal;
        if (TryGetClosestOpenPort(
                m_terrainCursor.Tile3i,
                out var snappedPort))
        {
            rawGoal = snappedPort.Center.CornerTile3f;
            endDirection = snappedPort.InboundNode.Direction;
            goalPort = snappedPort;
        }
        else
        {
            rawGoal = m_terrainCursor.Tile3f.SetZ(m_anchor.Z);
            endDirection = null;
            goalPort = null;
        }

        return m_trackPathFinder.GetGoalFromTile(
            rawGoal,
            m_anchor,
            CreateOptions(endDirection));
    }

    private bool TryGetClosestOpenPort(
        Tile3i cursor,
        out HighwayPort result)
    {
        var found = false;
        var bestDistance = long.MaxValue;
        result = default;
        foreach (var port in m_openPorts)
        {
            var distance = port.Center.Xy.DistanceSqrTo(cursor.Xy);
            if (distance > PortSnapMaxDistanceSquared ||
                (found && distance >= bestDistance))
            {
                continue;
            }

            found = true;
            bestDistance = distance;
            result = port;
        }

        return found;
    }

    private static bool AreSamePort(
        HighwayPort? left,
        HighwayPort? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return left.HasValue == right.HasValue;
        }

        return left.Value.Center == right.Value.Center &&
            left.Value.InboundNode == right.Value.InboundNode &&
            left.Value.OutboundNode == right.Value.OutboundNode;
    }

    private bool DoesCurrentPlanMateSelectedGoalPort()
    {
        if (!m_searchGoalPort.HasValue)
        {
            return true;
        }

        if (m_pieces.Count == 0)
        {
            return false;
        }

        var target = m_searchGoalPort.Value;
        var targetIsStillOpen = false;
        foreach (var openPort in m_openPorts)
        {
            if (AreSamePort(openPort, target))
            {
                targetIsStillOpen = true;
                break;
            }
        }

        if (!targetIsStillOpen)
        {
            return false;
        }

        var lastPiece = m_pieces[m_pieces.Count - 1];
        var physicalEndPortIndex = lastPiece.TrackDirection ==
            TrainTrackTrajectoryDirection.Backward
            ? 0
            : 1;
        var physicalEndPort = lastPiece.Proto.GetHighwayPort(
            physicalEndPortIndex,
            lastPiece.Transform);
        return physicalEndPort.IsExactMateOf(target);
    }

    private void RestartSearch()
    {
        m_searchInitialized = false;
        m_searchInProgress = false;
        m_previewDirty = true;
        m_searchGoalStartedAt = Time.unscaledTime;
        m_currentPlan = Option<TrainTrackPlan>.None;
        m_currentPlanIsExact = false;
        ShowLockedPlanOnly();
    }

    private void ShowLockedPlanOnly()
    {
        if (m_lockedPlan.HasValue)
        {
            SynchronizePlan(m_lockedPlan.Value);
        }
        else
        {
            m_pieces.Clear();
            ClearPreviews();
        }
    }

    private void SynchronizePlan(TrainTrackPlan plan)
    {
        m_pieces.Clear();
        if (m_builtPrefixStepCount < 0 ||
            m_builtPrefixStepCount > plan.Steps.Length)
        {
            Log.Error(
                "GroundRoads: invalid built-prefix length in continued " +
                "highway plan.");
            m_currentPlan = Option<TrainTrackPlan>.None;
            m_currentPlanIsExact = false;
            ClearPreviews();
            return;
        }

        var unbuiltStepCount =
            plan.Steps.Length - m_builtPrefixStepCount;
        if (unbuiltStepCount > MaximumPreviewPieces)
        {
            Log.Warning(
                $"GroundRoads: unbuilt highway suffix contains " +
                $"{unbuiltStepCount} pieces, exceeding the safe limit of " +
                $"{MaximumPreviewPieces}. Placement was cancelled.");
            m_currentPlan = Option<TrainTrackPlan>.None;
            m_currentPlanIsExact = false;
            ClearPreviews();
            return;
        }

        if (plan.ComputeSelfIntersection())
        {
            Log.Warning(
                "GroundRoads: highway plan intersects itself. Placement " +
                "was rejected before batch validation suppression.");
            m_currentPlan = Option<TrainTrackPlan>.None;
            m_currentPlanIsExact = false;
            ClearPreviews();
            return;
        }

        if (m_continuationStartNode.HasValue &&
            plan.Steps.Length > m_builtPrefixStepCount)
        {
            var firstStep = plan.Steps[m_builtPrefixStepCount];
            GetLogicalTrackNodes(
                firstStep,
                out var firstStartNode,
                out _);
            if (!firstStartNode.Equals(m_continuationStartNode.Value))
            {
                Log.Error(
                    "GroundRoads: continued highway plan does not start " +
                    "at the exact committed track tangent.");
                m_currentPlan = Option<TrainTrackPlan>.None;
                m_currentPlanIsExact = false;
                ClearPreviews();
                return;
            }
        }

        var count = plan.Steps.Length;

        for (var index = m_builtPrefixStepCount;
             index < count;
             index++)
        {
            var step = plan.Steps[index];
            if (!m_segmentsByTrackId.TryGetValue(
                    (step.Proto.Id.Value, step.Transform.IsReflected),
                    out var segment))
            {
                Log.Warning(
                    $"GroundRoads: no road mapping for train geometry " +
                    $"'{step.Proto.Id}'.");
                m_currentPlan = Option<TrainTrackPlan>.None;
                m_currentPlanIsExact = false;
                ClearPreviews();
                return;
            }

            m_pieces.Add(
                new Piece(
                    segment,
                    segment.MapTrackTransform(step.Transform),
                    step.TrackDirection));
        }

        if (!TryValidateRoadSeams(out var invalidSeamIndex))
        {
            Log.Error(
                $"GroundRoads: highway planner produced an incompatible " +
                $"road seam between pieces {invalidSeamIndex} and " +
                $"{invalidSeamIndex + 1}. Placement was cancelled.");
            m_currentPlan = Option<TrainTrackPlan>.None;
            m_currentPlanIsExact = false;
            ClearPreviews();
            return;
        }

        if (m_continuationPredecessor.HasValue &&
            m_pieces.Count > 0 &&
            !AreRoadPiecesContinuous(
                m_continuationPredecessor.Value,
                m_pieces[0]))
        {
            Log.Error(
                "GroundRoads: continued highway plan would create a lane " +
                "or angle break at the committed batch seam.");
            m_currentPlan = Option<TrainTrackPlan>.None;
            m_currentPlanIsExact = false;
            ClearPreviews();
            return;
        }

        SynchronizePreviews();
    }

    private bool TryValidateRoadSeams(out int invalidSeamIndex)
    {
        for (var index = 0; index + 1 < m_pieces.Count; index++)
        {
            if (!AreRoadPiecesContinuous(
                    m_pieces[index],
                    m_pieces[index + 1]))
            {
                invalidSeamIndex = index;
                return false;
            }
        }

        invalidSeamIndex = -1;
        return true;
    }

    private static bool AreRoadPiecesContinuous(Piece left, Piece right)
    {
        var leftNodes = GetGraphNodes(left);
        var rightNodes = GetGraphNodes(right);
        if (leftNodes.Length < 4 || rightNodes.Length < 4)
        {
            return false;
        }

        // Lane 0 follows the physical source-track orientation; lane 1 runs
        // against it. Some train-plan pieces use a prototype backwards, so
        // choose the directed lane endpoints in logical plan order first.
        var leftIsBackward = left.TrackDirection ==
                             TrainTrackTrajectoryDirection.Backward;
        var rightIsBackward = right.TrackDirection ==
                              TrainTrackTrajectoryDirection.Backward;
        var leftForwardEnd = leftIsBackward
            ? leftNodes[3]
            : leftNodes[1];
        var leftReverseStart = leftIsBackward
            ? leftNodes[0]
            : leftNodes[2];
        var rightForwardStart = rightIsBackward
            ? rightNodes[2]
            : rightNodes[0];
        var rightReverseEnd = rightIsBackward
            ? rightNodes[1]
            : rightNodes[3];

        // Validate both directed lanes exactly so coincident but
        // wrong-facing nodes cannot make vehicles jump or cut a curve.
        return leftForwardEnd == rightForwardStart &&
               rightReverseEnd == leftReverseStart;
    }

    private static ImmutableArray<RoadGraphNodeKey> GetGraphNodes(
        Piece piece)
    {
        var nodes = new ImmutableArrayBuilder<RoadGraphNodeKey>(
            piece.Proto.LanesData.Length * 2);
        for (var laneIndex = 0;
             laneIndex < piece.Proto.LanesData.Length;
             laneIndex++)
        {
            nodes[laneIndex * 2] =
                piece.Proto.GetTransformedStartGraphNode(
                laneIndex,
                piece.Transform);
            nodes[laneIndex * 2 + 1] =
                piece.Proto.GetTransformedEndGraphNode(
                laneIndex,
                piece.Transform);
        }

        return nodes.GetImmutableArrayAndClear();
    }

    private void SynchronizePreviews()
    {
        for (var index = 0; index < m_pieces.Count; index++)
        {
            var piece = m_pieces[index];
            if (index < m_previews.Count &&
                m_previews[index].EntityProto.Id == piece.Proto.Id)
            {
                m_previews[index].SetTransform(piece.Transform);
                continue;
            }

            if (index < m_previews.Count)
            {
                m_previews[index].DestroyAndReturnToPool();
                m_previews[index] = CreatePreview(piece);
            }
            else
            {
                m_previews.Add(CreatePreview(piece));
            }
        }

        for (var index = m_previews.Count - 1;
             index >= m_pieces.Count;
             index--)
        {
            m_previews[index].DestroyAndReturnToPool();
            m_previews.RemoveAt(index);
        }
    }

    private LayoutEntityPreview CreatePreview(Piece piece)
    {
        return m_previewManager.CreatePreview(
            piece.Proto,
            EntityPlacementPhase.FirstAndFinal,
            piece.Transform,
            disableValidation: false,
            disablePortPreviews: false,
            enableMiniZipperPlacement: false,
            isForRemoval: false,
            disablePortPreviewsPredicate: null,
            disableHighlight: false);
    }

    private bool AllPreviewsValid()
    {
        return m_previews.Count > 0 && m_previews.All(
            preview => preview.ValidationResult.HasValue &&
                       preview.ValidationResult.Value.IsSuccess);
    }

    private bool TryScheduleBuild(
        TrainTrackPlan plan,
        bool continueAfterBuild)
    {
        SynchronizePlan(plan);
        if (m_pieces.Count == 0 ||
            !AllPreviewsValid() ||
            !TryGetExactPlanEndNode(plan, out var continuationNode) ||
            !DoesCurrentPlanMateSelectedGoalPort())
        {
            if (m_searchGoalPort.HasValue)
            {
                Log.Warning(
                    "GroundRoads: snapped target port changed or no longer " +
                    "mates the planned highway end; placement was rejected.");
                RestartSearch();
            }

            return false;
        }

        var continuationLastPiece = m_pieces[m_pieces.Count - 1];
        var pieceCount = m_pieces.Count;
        var configs =
            new ImmutableArrayBuilder<EntityConfigData>(pieceCount);
        for (var index = 0; index < pieceCount; index++)
        {
            var piece = m_pieces[index];
            configs[index] = new EntityConfigData(
                piece.Proto,
                m_configContext)
            {
                Transform = piece.Transform
            };
        }

        // Adjacent pieces intentionally share their exact seam. The train
        // planner has already validated the route; generic batch validation
        // must not discard alternating curve/straight pieces because of that
        // planned overlap.
        var command = m_inputScheduler.ScheduleInputCmd(
            new BatchCreateStaticEntitiesCmd(
                configs.GetImmutableArrayAndClear(),
                BuildMiniZippersMode.Never,
                isFree: false,
                allowValidationSuppression: true,
                applyConfiguration: false));

        m_pendingBuildCommand = command;
        m_continueAfterPendingBuild = continueAfterBuild;
        m_pendingContinuationPlan = plan;
        m_pendingContinuationNode = continuationNode;
        m_pendingContinuationLastPiece = continuationLastPiece;
        ResetPlanningState();

        Log.Info(
            $"GroundRoads: scheduled {pieceCount} train-planned highway " +
            $"pieces (continue: {continueAfterBuild}).");
        return true;
    }

    private void ResetPlan()
    {
        m_pendingBuildCommand = null;
        m_continueAfterPendingBuild = false;
        ResetPlanningState();
    }

    private void ResetPlanningState()
    {
        m_hasStart = false;
        m_searchInitialized = false;
        m_searchInProgress = false;
        m_previewDirty = true;
        m_currentPlanIsExact = false;
        m_automaticStartDirection = false;
        m_lockedPlan = Option<TrainTrackPlan>.None;
        m_currentPlan = Option<TrainTrackPlan>.None;
        m_forcedStartDirection = null;
        m_forcedEndDirection = null;
        m_searchEndDirection = null;
        m_searchGoalPort = null;
        m_pendingPrimaryEndDirection = null;
        m_pendingPrimaryGoalPort = null;
        m_continuationStartNode = null;
        m_continuationPredecessor = null;
        m_builtPrefixStepCount = 0;
        m_directionIndex = 0;
        m_secondaryPressTracking = false;
        m_primaryPressTracking = false;
        InvalidateConfirmationClick();
        InvalidatePendingPrimaryClick();
        m_pieces.Clear();
        ClearPreviews();
    }

    private void ClearPreviews()
    {
        foreach (var preview in m_previews)
        {
            preview.DestroyAndReturnToPool();
        }

        m_previews.Clear();
    }
}

public sealed class GroundRoadToolbarRegistrator : IHotReloadUi
{
    private readonly GroundRoadDragController m_controller;
    private readonly HighwayTIntersectionPlacementController
        m_tIntersectionController;
    private readonly HighwayCrossIntersectionPlacementController
        m_crossIntersectionController;
    private readonly HighwayRoundaboutPlacementController
        m_roundaboutController;

    public GroundRoadToolbarRegistrator(
        ToolbarHud hud,
        UiContext context,
        ProtosDb protosDb,
        GroundRoadDragController controller,
        HighwayTIntersectionPlacementController tIntersectionController,
        HighwayCrossIntersectionPlacementController crossIntersectionController,
        HighwayRoundaboutPlacementController roundaboutController)
    {
        m_controller = controller;
        m_tIntersectionController = tIntersectionController;
        m_crossIntersectionController = crossIntersectionController;
        m_roundaboutController = roundaboutController;
        var representative = protosDb.All<HighwaySegmentProto>().First();
        var vehicleCategory = protosDb.GetOrThrow<ToolbarCategoryProto>(
            GroundRoadIds.ToolbarCategory);
        var categories = ImmutableArray.Create(
            new ToolbarEntryData(vehicleCategory, order: 0));
        var name = Loc.Str(
            "GroundRoads_HighwayTool_Name",
            "Autobahn bauen",
            "toolbar name for the train-planned highway tool");
        var description = Loc.Str(
            "GroundRoads_HighwayTool_Description",
            "Baut Autobahnen mit dem Kurven- und Pivotplaner des " +
            "Schienen-DLC. Linksklick setzt Punkte, Doppelklick baut, " +
            "Rechtsklick bricht ab. Shift beim Doppelklick setzt am " +
            "Endpunkt nahtlos fort. Der Verkehrsdirektor verbindet " +
            "Fahrzeuge automatisch mit der nächsten sinnvollen Autobahn.",
            "toolbar description for the train-planned highway tool");

        hud.AddItem(
            new ControllerToolbarMenuItem(
                context,
                controller,
                name,
                representative.IconPath,
                categories,
                extraLockingProto: null,
                groupProto: null,
                popupProto: representative,
                floaterTitle: name,
                floaterDesc: description,
                order: 0));

        AddNodeItem(
            hud,
            context,
            tIntersectionController,
            tIntersectionController.Prototype,
            categories,
            "GroundRoads_TIntersectionTool_Name",
            "T-Kreuzung bauen",
            "Platziert eine dreiseitige Kreuzung. Rastet nur an freien " +
            "Enden normaler Autobahnsegmente ein; zwischen zwei Knoten " +
            "muss ein kurzes Autobahnstück liegen. Drehen ändert die " +
            "Ausrichtung.",
            order: 10);
        AddNodeItem(
            hud,
            context,
            crossIntersectionController,
            crossIntersectionController.Prototype,
            categories,
            "GroundRoads_CrossIntersectionTool_Name",
            "+-Kreuzung bauen",
            "Platziert eine vierseitige ungeregelte Kreuzung mit Geradeaus-, " +
            "Links- und Rechtsabbiegern. Zwischen zwei Knoten muss ein " +
            "kurzes Autobahnstück liegen.",
            order: 20);
        AddNodeItem(
            hud,
            context,
            roundaboutController,
            roundaboutController.Prototype,
            categories,
            "GroundRoads_RoundaboutTool_Name",
            "Kreisverkehr bauen",
            "Platziert einen vierarmigen Kreisverkehr für Rechtsverkehr. " +
            "Alle Bewegungen folgen derselben Kreisrichtung. Zwischen zwei " +
            "Knoten muss ein kurzes Autobahnstück liegen.",
            order: 30);

    }

    private static void AddNodeItem<TController>(
        ToolbarHud hud,
        UiContext context,
        TController controller,
        HighwayJunctionProto proto,
        ImmutableArray<ToolbarEntryData> categories,
        string localizationId,
        string displayName,
        string description,
        int order)
        where TController : HighwayNodePlacementControllerBase
    {
        var name = Loc.Str(
            localizationId,
            displayName,
            "toolbar name for a GroundRoads highway node tool");
        hud.AddItem(
            new ControllerToolbarMenuItem(
                context,
                controller,
                name,
                proto.IconPath,
                categories,
                extraLockingProto: null,
                groupProto: null,
                popupProto: proto,
                floaterTitle: name,
                floaterDesc: Loc.Str(
                    localizationId + "_Description",
                    description,
                    "toolbar description for a GroundRoads highway node tool"),
                order: order));
    }

    public void DisposeForHotReload()
    {
        m_controller.DisposeForHotReload();
        m_tIntersectionController.DisposeForHotReload();
        m_crossIntersectionController.DisposeForHotReload();
        m_roundaboutController.DisposeForHotReload();
    }
}
