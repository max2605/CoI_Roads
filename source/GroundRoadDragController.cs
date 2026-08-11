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
using Mafi.Core.Terrain;
using Mafi.Core.Trains;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.Ui.Hud.Toolbar.MenuItems;
using Mafi.Unity.UiToolkit.Component;
using UnityEngine;
using UnityEngine.EventSystems;
using EntityId = Mafi.Core.EntityId;

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
    private const int PortSnapMaxDistanceSquared = 9;
    private const int ExtendedPortSnapAnchorRange = 8;
    private const int ExtendedPortSnapAnchorMaxDistanceSquared =
        ExtendedPortSnapAnchorRange * ExtendedPortSnapAnchorRange;
    // Half a tile accounts for integer cursor rounding at diagonal headings;
    // the next lattice point beyond the advertised 24 tiles still fails.
    private const double HighwayEndExtensionSnapLength = 24.5;
    private const double HighwayEndExtensionSnapHalfWidth = 3.0;
    private const int RoadSurfaceCollisionBucketSize = 8;
    private const double RoadSurfaceSampleSpacing = 0.5;
    private const double MaximumRoadSurfaceCollisionDistance = 4.0;
    private const double GroundSupportToleranceTiles = 1.0;
    private const double TerrainPenetrationToleranceTiles = 0.02;
    private const double MinimumVisibleSupportHeightTiles = 0.25;

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

    private readonly struct RoadSurfaceSegment
    {
        public readonly double StartX;
        public readonly double StartY;
        public readonly double StartZ;
        public readonly double EndX;
        public readonly double EndY;
        public readonly double EndZ;
        public readonly EntityId EntityId;
        public readonly bool AllowsStartAttachment;
        public readonly bool AllowsEndAttachment;
        public readonly double HalfWidth;

        public RoadSurfaceSegment(
            double startX,
            double startY,
            double startZ,
            double endX,
            double endY,
            double endZ,
            EntityId entityId,
            bool allowsStartAttachment,
            bool allowsEndAttachment,
            double halfWidth = 1.0)
        {
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            EndX = endX;
            EndY = endY;
            EndZ = endZ;
            EntityId = entityId;
            AllowsStartAttachment = allowsStartAttachment;
            AllowsEndAttachment = allowsEndAttachment;
            HalfWidth = halfWidth;
        }
    }

    private readonly struct SurfacePoint2d
    {
        public readonly double X;
        public readonly double Y;

        public SurfacePoint2d(double x, double y)
        {
            X = x;
            Y = y;
        }

        public SurfacePoint2d Lerp(SurfacePoint2d other, double progress)
        {
            return new SurfacePoint2d(
                X + (other.X - X) * progress,
                Y + (other.Y - Y) * progress);
        }
    }

    private readonly struct RoadSurfaceVertex
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public RoadSurfaceVertex(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public RoadSurfaceVertex(Tile3f point)
            : this(
                point.X.ToDouble(),
                point.Y.ToDouble(),
                point.Z.ToDouble())
        {
        }

        public SurfacePoint2d Xy => new(X, Y);
    }

    private readonly struct RoadSurfacePlane
    {
        public readonly double XFactor;
        public readonly double YFactor;
        public readonly double Offset;

        public RoadSurfacePlane(
            double xFactor,
            double yFactor,
            double offset)
        {
            XFactor = xFactor;
            YFactor = yFactor;
            Offset = offset;
        }

        public double GetHeight(SurfacePoint2d point)
        {
            return XFactor * point.X + YFactor * point.Y + Offset;
        }
    }

    private readonly struct BilinearTerrainPatch
    {
        public readonly Tile2i Cell;
        public readonly double BottomLeft;
        public readonly double BottomRight;
        public readonly double TopLeft;
        public readonly double TopRight;

        public BilinearTerrainPatch(
            Tile2i cell,
            HeightTilesF bottomLeft,
            HeightTilesF bottomRight,
            HeightTilesF topLeft,
            HeightTilesF topRight)
        {
            Cell = cell;
            BottomLeft = bottomLeft.Value.ToDouble();
            BottomRight = bottomRight.Value.ToDouble();
            TopLeft = topLeft.Value.ToDouble();
            TopRight = topRight.Value.ToDouble();
        }

        public double GetHeight(SurfacePoint2d point)
        {
            var x = point.X - Cell.X;
            var y = point.Y - Cell.Y;
            return BottomLeft * (1.0 - x) * (1.0 - y) +
                BottomRight * x * (1.0 - y) +
                TopLeft * (1.0 - x) * y +
                TopRight * x * y;
        }
    }

    private readonly IInputScheduler m_inputScheduler;
    private readonly IUnityInputMgr m_inputManager;
    private readonly ShortcutsManager m_shortcuts;
    private readonly TerrainCursor m_terrainCursor;
    private readonly LayoutEntityPreviewManager m_previewManager;
    private readonly ConfigSerializationContext m_configContext;
    private readonly ITrainTrackPathFinder m_trackPathFinder;
    private readonly TerrainManager m_terrainManager;
    private readonly TerrainOccupancyManager m_terrainOccupancyManager;
    private readonly IEntitiesManager m_entitiesManager;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly Toolbox m_elevationToolbox;
    private readonly Dictionary<(
        string TrackId,
        bool Reflected,
        HighwayTier Tier),
        HighwaySegmentProto> m_segmentsByTrackId;
    private readonly List<Piece> m_pieces = new();
    private readonly List<LayoutEntityPreview> m_previews = new();
    private readonly Lyst<HighwayPort> m_allOpenPorts = new();
    private readonly Lyst<HighwayPort> m_openPorts = new();
    private readonly Lyst<EntityId> m_roadSurfaceCollisionIds = new();
    private IUnityInputController m_activeFacade;

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
    private bool m_startAttachmentPortIsOpen;
    private bool m_searchGoalPortIsOpen;
    private Tile3f m_anchor;
    private Tile3f m_searchGoal;
    private Tile3f m_lastConfirmationEndpoint;
    private Tile3f m_pendingPrimaryGoal;
    private HighwayPort? m_searchGoalPort;
    private HighwayPort? m_startAttachmentPort;
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
    private int m_flatEndDirectionBaseIndex;
    private int m_flatEndDirectionAttemptCount;
    private int m_lastConfirmationStepCount;
    private float m_lastConfirmationClickTime;
    private float m_pendingPrimaryClickTime;
    private float m_searchGoalStartedAt;
    private HighwayTier m_selectedTier = HighwayTier.Standard;
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
        TerrainManager terrainManager,
        TerrainOccupancyManager terrainOccupancyManager,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        ProtosDb protosDb,
        ToolbarHud hud)
    {
        m_inputScheduler = context.InputScheduler;
        m_inputManager = context.InputMgr;
        m_shortcuts = context.ShortcutsManager;
        m_terrainCursor = terrainCursor.Instance;
        m_previewManager = previewManager;
        m_configContext = configContext;
        m_trackPathFinder = trackPathFinder;
        m_terrainManager = terrainManager;
        m_terrainOccupancyManager = terrainOccupancyManager;
        m_entitiesManager = entitiesManager;
        m_simLoopEvents = simLoopEvents;
        m_segmentsByTrackId = protosDb.All<HighwaySegmentProto>()
            .ToDictionary(
                x => (
                    x.SourceTrackProto.Id.Value,
                    x.CorrectsReflectedHandedness,
                    x.Tier),
                x => x);
        m_elevationToolbox = hud.CreateToolbox();
        m_elevationToolbox.AddEntry(
            "Assets/Unity/UserInterface/General/PlatformUp128.png",
            shortcuts => shortcuts.RaiseUp,
            () => ChangeTerrainCursorHeight(raise: true),
            tooltip: null);
        m_elevationToolbox.AddEntry(
            "Assets/Unity/UserInterface/General/PlatformDown128.png",
            shortcuts => shortcuts.LowerDown,
            () => ChangeTerrainCursorHeight(raise: false),
            tooltip: null);
        m_elevationToolbox.Hide();
        m_simLoopEvents.Sync.AddNonSaveable(this, SyncUpdate);
    }

    public void Activate()
    {
        if (m_isActive)
        {
            return;
        }

        m_isActive = true;
        m_activeFacade ??= this;
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
        m_terrainCursor.RelativeHeight = ThicknessTilesI.Zero;
        m_elevationToolbox.Show();
        Log.Info(
            $"GroundRoads: train-planned {m_selectedTier} highway tool " +
            "activated.");
    }

    internal HighwayTier SelectedTier => m_selectedTier;

    internal void SelectTier(HighwayTier tier)
    {
        if (m_isActive && m_selectedTier != tier)
        {
            Deactivate();
        }

        m_selectedTier = tier;
    }

    internal void SetActiveFacade(IUnityInputController facade)
    {
        m_activeFacade = facade;
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
        m_elevationToolbox.Hide();
        m_terrainCursor.Deactivate();
        m_activeFacade = null;
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

        // Match the train-track construction tool exactly: use the configured
        // RaiseUp/LowerDown bindings (E/Q by default), move one height tile per
        // key press, and clamp the cursor to the native pillar height range.
        var handledElevationShortcut = false;
        if (m_shortcuts.IsDown(m_shortcuts.LowerDown))
        {
            handledElevationShortcut = true;
            ChangeTerrainCursorHeight(raise: false);
        }
        if (m_shortcuts.IsDown(m_shortcuts.RaiseUp))
        {
            handledElevationShortcut = true;
            ChangeTerrainCursorHeight(raise: true);
        }

        // Keep the elevation controls live while a completed route is being
        // synchronized. This mirrors the track tool and lets a continued
        // route start at the height the player already selected.
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
                m_inputManager.DeactivateController(
                    m_activeFacade ?? this);
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
            ResetFlatEndDirectionRetries();
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
            ResetFlatEndDirectionRetries();
            RestartSearch();
            return true;
        }

        if (!m_terrainCursor.HasValue)
        {
            ClearPreviews();
            m_previewDirty = true;
            return handledElevationShortcut;
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
                    m_startAttachmentPort = snappedPort;
                    m_startAttachmentPortIsOpen = true;
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
                    m_startAttachmentPort = null;
                    m_startAttachmentPortIsOpen = false;
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

            return handledElevationShortcut;
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

        return handledElevationShortcut;
    }

    private void ChangeTerrainCursorHeight(bool raise)
    {
        if (!m_isActive)
        {
            return;
        }

        var current = m_terrainCursor.RelativeHeight;
        var adjusted = GetAdjustedRelativeHeight(current, raise);
        if (adjusted == current)
        {
            return;
        }

        m_terrainCursor.RelativeHeight = adjusted;
        InvalidateConfirmationClick();
        InvalidatePendingPrimaryClick();
        ResetFlatEndDirectionRetries();
        if (m_hasStart)
        {
            RestartSearch();
        }
        else
        {
            m_previewDirty = true;
        }
    }

    private static ThicknessTilesI GetAdjustedRelativeHeight(
        ThicknessTilesI current,
        bool raise)
    {
        return raise
            ? (current + ThicknessTilesI.One)
                .Min(TrainTrackPillarProto.MAX_PILLAR_HEIGHT)
            : (current - ThicknessTilesI.One).Max(ThicknessTilesI.Zero);
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
            m_allOpenPorts.Clear();
            m_openPorts.Clear();
            m_startAttachmentPortIsOpen = false;
            m_searchGoalPortIsOpen = false;
            return;
        }

        HighwayPortDiscovery.FillOpenPorts(
            m_entitiesManager,
            includeJunctionPorts: true,
            result: m_allOpenPorts);

        m_openPorts.Clear();
        m_startAttachmentPortIsOpen = false;
        m_searchGoalPortIsOpen = false;
        foreach (var openPort in m_allOpenPorts)
        {
            if (openPort.Tier != m_selectedTier)
            {
                continue;
            }

            if (IsPortWithinSnapSearchRange(
                    openPort,
                    m_terrainCursor.Tile3i))
            {
                m_openPorts.Add(openPort);
            }

            m_startAttachmentPortIsOpen |= AreSamePort(
                openPort,
                m_startAttachmentPort);
            m_searchGoalPortIsOpen |= AreSamePort(
                openPort,
                m_searchGoalPort);
        }
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
        GetLogicalRoadPorts(
            predecessor,
            out _,
            out var predecessorEndPort);
        m_startAttachmentPort = predecessorEndPort;
        m_startAttachmentPortIsOpen = true;
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
            !TryGetExactPlanEndNode(plan, out var endNode) ||
            endNode.Direction.GradeFactor != TrainTrackGradeFactor.G0)
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
            if (candidate.Direction == direction.Direction)
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
        // TerrainCursor supplies the requested road height. The train planner
        // may now insert gentle ramp pieces between flat pivots at different
        // elevations.
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

        var goalChanged = goal != m_searchGoal ||
            endDirection != m_searchEndDirection ||
            !AreSamePort(goalPort, m_searchGoalPort);
        if (goalChanged)
        {
            m_searchGoal = goal;
            m_searchEndDirection = endDirection;
            m_searchGoalPort = goalPort;
            m_searchGoalPortIsOpen = goalPort.HasValue;
            ResetFlatEndDirectionRetries();
            RestartSearch();
        }

        UpdateAutomaticStartDirection(directionGoal);
        var options = CreateOptions(m_forcedEndDirection, goal);

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
            var isExact = !approximate &&
                result.Value.LastStepEndPosition.HasValue &&
                result.Value.LastStepEndPosition.Value == goal;
            if (isExact &&
                !m_forcedEndDirection.HasValue &&
                TryGetExactPlanEndNode(result.Value, out var endNode) &&
                endNode.Direction.GradeFactor !=
                    TrainTrackGradeFactor.G0 &&
                TryCreateFlatDirection(
                    endNode.Direction,
                    out var flatEndDirection))
            {
                // GoalMustBeFlat is currently ignored by the v0.8.6c train
                // path finder. Re-run the exact solution with the same XY
                // tangent explicitly forced to G0, so every committed pivot
                // has a safe horizontal terrain seam.
                m_flatEndDirectionBaseIndex =
                    GetDirectionIndex(flatEndDirection);
                m_flatEndDirectionAttemptCount = 0;
                TrySelectNextFlatEndDirection();
                RestartSearch();
                return;
            }

            m_currentPlan = result;
            m_currentPlanIsExact = isExact;
            SynchronizePlan(result.Value);
            if (isExact &&
                m_currentPlan.HasValue &&
                !HasRoadSurfaceClearance(result.Value))
            {
                if (TryRetryFlatEndDirectionAfterClearanceFailure(
                        result.Value))
                {
                    return;
                }

                Log.Warning(
                    "GroundRoads: rejected a highway plan because an " +
                    "endpoint port is unavailable, its full road-profile " +
                    "surface is buried by terrain, water/map bounds are " +
                    "crossed, or " +
                    "an entity intersects the corridor. Free the port, " +
                    "grade/remove blocking terrain, clear the corridor, " +
                    "or move/add a pivot.");
                m_currentPlan = Option<TrainTrackPlan>.None;
                m_currentPlanIsExact = false;
                InvalidatePendingPrimaryClick();
                ShowLockedPlanOnly();
                return;
            }

            if (!isExact && !needsMore)
            {
                if (TryAdvanceFlatEndDirectionForUnresolvedSearch())
                {
                    return;
                }

                m_currentPlan = Option<TrainTrackPlan>.None;
                m_currentPlanIsExact = false;
                InvalidatePendingPrimaryClick();
                ShowLockedPlanOnly();
                return;
            }
        }
        else if (noRoute)
        {
            if (TryAdvanceFlatEndDirectionForUnresolvedSearch())
            {
                return;
            }

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
            if (TryAdvanceFlatEndDirectionForUnresolvedSearch())
            {
                Log.Info(
                    "GroundRoads: timed-out flat endpoint direction; " +
                    "trying the next closest G0 heading.");
                return;
            }

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
        ResetFlatEndDirectionRetries();
        RestartSearch();
    }

    private TrainTrackPathFinderOptions CreateOptions(
        TrainTrackNodeDirection? forcedEndDirection,
        Tile3f goal)
    {
        // Prevent the native near-goal singularity mode from limiting a
        // route to one direction/radius change. This is what enables
        // repeated left/right bends and true S-curves.
        return new TrainTrackPathFinderOptions(
            preferredHeight: GetPreferredRampHeight(m_anchor, goal),
            forcedStartDirectionA: m_forcedStartDirection,
            forcedEndDirectionA: forcedEndDirection,
            buildDirection: TrainTrackTrajectoryDirection.Bidirectional,
            flags: CreateHighwayPathFinderFlags());
    }

    private static HeightTilesI? GetPreferredRampHeight(
        Tile3f start,
        Tile3f goal)
    {
        var startHeight = start.Tile3iRounded.Z;
        var goalHeight = goal.Tile3iRounded.Z;
        return startHeight == goalHeight
            ? (HeightTilesI?)null
            : new HeightTilesI(Math.Max(startHeight, goalHeight));
    }

    private static TrainTrackPathFinderFlags CreateHighwayPathFinderFlags()
    {
        // The native terrain-ramp library connects its G8 (12.5%) and G4
        // (25%) pieces, so both grades remain available and the planner can
        // choose the gentler option when enough horizontal space exists.
        // Its collision pass still hard-codes the train track's 0.25-metre
        // ground tolerance, which rejects normally terraced dumped terrain
        // before our full-width road-surface validator can apply the highway's
        // one-tile follow range. Let the planner explore those candidates;
        // HasRoadSurfaceClearance then performs the stricter road-specific
        // terrain, water, map-edge, entity, and existing-highway checks before
        // any preview can be committed.
        // GoalMustBeFlat is retained for engine versions that honor it; the
        // v0.8.6c fallback above additionally forces and verifies G0.
        return TrainTrackPathFinderFlags.IgnoreCollisions |
               TrainTrackPathFinderFlags.GoalMustBeFlat |
               TrainTrackPathFinderFlags.AlternativeMode;
    }

    private static bool TryCreateFlatDirection(
        TrainTrackNodeDirection source,
        out TrainTrackNodeDirection result)
    {
        return TrainTrackNodeDirection.TryCreateFromDirection(
            source.Direction.Vector2f,
            TrainTrackGradeFactor.G0,
            out result,
            out _);
    }

    private void ResetFlatEndDirectionRetries()
    {
        m_flatEndDirectionBaseIndex = 0;
        m_flatEndDirectionAttemptCount = 0;
        m_forcedEndDirection = m_searchEndDirection;
    }

    private bool TrySelectNextFlatEndDirection()
    {
        if (m_flatEndDirectionAttemptCount >= 16)
        {
            return false;
        }

        var directionIndex = GetFlatEndDirectionRetryIndex(
            m_flatEndDirectionBaseIndex,
            m_flatEndDirectionAttemptCount);
        m_flatEndDirectionAttemptCount++;
        if (!TrainTrackNodeDirection.TryCreateFromAngle(
                (directionIndex * 22.5).Degrees(),
                TrainTrackGradeFactor.G0,
                out var direction,
                out _))
        {
            return false;
        }

        m_forcedEndDirection = direction;
        return true;
    }

    private bool TryRetryFlatEndDirection()
    {
        if (!CanRetryFlatEndDirection(
                m_searchEndDirection.HasValue,
                m_flatEndDirectionAttemptCount) ||
            !TrySelectNextFlatEndDirection())
        {
            return false;
        }

        RestartSearch();
        return true;
    }

    private bool TryAdvanceFlatEndDirectionForUnresolvedSearch()
    {
        if (m_searchEndDirection.HasValue)
        {
            return false;
        }

        if (m_flatEndDirectionAttemptCount > 0)
        {
            return TryRetryFlatEndDirection();
        }

        var delta = m_searchGoal - m_anchor;
        if (!CanInitializeFlatEndDirectionRetries(
                m_searchEndDirection.HasValue,
                m_flatEndDirectionAttemptCount,
                !delta.X.IsZero || !delta.Y.IsZero) ||
            !TrainTrackNodeDirection.TryCreateFromAngle(
                delta.Angle,
                TrainTrackGradeFactor.G0,
                out var directDirection,
                out _))
        {
            return false;
        }

        m_flatEndDirectionBaseIndex =
            GetDirectionIndex(directDirection);
        m_flatEndDirectionAttemptCount = 0;
        if (!TrySelectNextFlatEndDirection())
        {
            return false;
        }

        RestartSearch();
        return true;
    }

    private static bool CanInitializeFlatEndDirectionRetries(
        bool hasExplicitEndDirection,
        int attemptCount,
        bool hasHorizontalGoalDelta)
    {
        return !hasExplicitEndDirection &&
               attemptCount == 0 &&
               hasHorizontalGoalDelta;
    }

    private bool TryRetryFlatEndDirectionAfterClearanceFailure(
        TrainTrackPlan rejectedPlan)
    {
        if (!m_searchEndDirection.HasValue &&
            m_flatEndDirectionAttemptCount == 0 &&
            TryGetExactPlanEndNode(rejectedPlan, out var endNode) &&
            endNode.Direction.GradeFactor == TrainTrackGradeFactor.G0)
        {
            // An unrestricted search can already return a flat end. Count
            // that heading as the first attempted direction, then continue
            // with the nearest alternatives when its full-width corridor is
            // blocked.
            m_flatEndDirectionBaseIndex =
                GetDirectionIndex(endNode.Direction);
            m_flatEndDirectionAttemptCount = 1;
        }

        return TryRetryFlatEndDirection();
    }

    private static bool CanRetryFlatEndDirection(
        bool hasExplicitEndDirection,
        int attemptCount)
    {
        return !hasExplicitEndDirection &&
               attemptCount > 0 &&
               attemptCount < 16;
    }

    private static int GetFlatEndDirectionRetryIndex(
        int baseIndex,
        int attempt)
    {
        // Closest heading first: 0, +1, -1, +2, -2, ... +8.
        var offset = attempt == 0
            ? 0
            : (attempt + 1) / 2 * (attempt % 2 == 1 ? 1 : -1);
        return (baseIndex + offset) & 15;
    }

    private bool HasRoadSurfaceClearance(TrainTrackPlan plan)
    {
        return AreAuthorizedAttachmentPortsOpen() &&
               HasRoadSurfaceTerrainClearance() &&
               HasRoadSurfaceEntityClearance(plan);
    }

    private bool HasRoadSurfaceTerrainClearance()
    {
        foreach (var piece in m_pieces)
        {
            for (var laneIndex = 0;
                 laneIndex < piece.Proto.LanesTrajectories.Length;
                 laneIndex++)
            {
                var lane = piece.Proto.LanesTrajectories[laneIndex];
                for (var index = 1;
                     index < lane.LaneCenterSamples.Length;
                     index++)
                {
                    var start = lane.LaneCenterSamples[index - 1];
                    var end = lane.LaneCenterSamples[index];
                    var startDirection =
                        lane.LaneDirectionSamples[index - 1];
                    var endDirection = lane.LaneDirectionSamples[index];
                    var worldStart = piece.Proto.Layout
                        .TransformPoint_RelToCenterTile(
                            start,
                            piece.Transform);
                    var worldEnd = piece.Proto.Layout
                        .TransformPoint_RelToCenterTile(
                            end,
                            piece.Transform);
                    var worldStartDirection = piece.Proto.Layout
                        .TransformDirection(
                            startDirection,
                            piece.Transform);
                    var worldEndDirection = piece.Proto.Layout
                        .TransformDirection(
                            endDirection,
                            piece.Transform);
                    if (!DoesRoadLaneStripMatchTerrain(
                            worldStart,
                            worldEnd,
                            worldStartDirection,
                            worldEndDirection,
                            piece.Transform.IsReflected,
                            allowAirBelow: true,
                            piece.Proto.VisualLaneWidthTiles / 2.0))
                    {
                        return false;
                    }
                }
            }
        }

        return m_pieces.Count > 0;
    }

    private bool DoesRoadLaneStripMatchTerrain(
        Tile3f start,
        Tile3f end,
        RelTile3f startDirection,
        RelTile3f endDirection,
        bool isReflected,
        bool allowAirBelow,
        double laneHalfWidthTiles)
    {
        var triangles = CreateRoadSurfaceStripTriangles(
            start,
            end,
            startDirection,
            endDirection,
            isReflected,
            laneHalfWidthTiles);
        return DoesRoadSurfaceTriangleMatchTerrain(
                   triangles.FirstTriangleFirst,
                   triangles.FirstTriangleSecond,
                   triangles.FirstTriangleThird,
                   allowAirBelow) &&
               DoesRoadSurfaceTriangleMatchTerrain(
                   triangles.SecondTriangleFirst,
                   triangles.SecondTriangleSecond,
                   triangles.SecondTriangleThird,
                   allowAirBelow);
    }

    private static (
        RoadSurfaceVertex FirstTriangleFirst,
        RoadSurfaceVertex FirstTriangleSecond,
        RoadSurfaceVertex FirstTriangleThird,
        RoadSurfaceVertex SecondTriangleFirst,
        RoadSurfaceVertex SecondTriangleSecond,
        RoadSurfaceVertex SecondTriangleThird)
        CreateRoadSurfaceStripTriangles(
            Tile3f start,
            Tile3f end,
            RelTile3f startDirection,
            RelTile3f endDirection,
            bool isReflected)
    {
        return CreateRoadSurfaceStripTriangles(
            start,
            end,
            startDirection,
            endDirection,
            isReflected,
            laneHalfWidthTiles: 1.0);
    }

    private static (
        RoadSurfaceVertex FirstTriangleFirst,
        RoadSurfaceVertex FirstTriangleSecond,
        RoadSurfaceVertex FirstTriangleThird,
        RoadSurfaceVertex SecondTriangleFirst,
        RoadSurfaceVertex SecondTriangleSecond,
        RoadSurfaceVertex SecondTriangleThird)
        CreateRoadSurfaceStripTriangles(
            Tile3f start,
            Tile3f end,
            RelTile3f startDirection,
            RelTile3f endDirection,
            bool isReflected,
            double laneHalfWidthTiles)
    {
        // MeshBuilder normalizes each 3D direction and then crosses it with
        // Vector3.up without renormalizing in XY. Using the same projected
        // lateral vector keeps this validation byte-for-byte aligned with
        // the visible lane cross-section on G4/G8 slopes.
        var startLateral = startDirection.Normalized.Xy
            .RightOrthogonalVector;
        var endLateral = endDirection.Normalized.Xy
            .RightOrthogonalVector;
        if (isReflected)
        {
            // Reflection reverses handedness: R(Md) = -M(Rd). The model is
            // extruded in local space before its entity transform is
            // reflected, so swap the world-space lateral sign to preserve
            // MeshBuilder's original vertex indices and diagonal.
            startLateral = -startLateral;
            endLateral = -endLateral;
        }
        var startX = start.X.ToDouble();
        var startY = start.Y.ToDouble();
        var startZ = start.Z.ToDouble();
        var endX = end.X.ToDouble();
        var endY = end.Y.ToDouble();
        var endZ = end.Z.ToDouble();
        var startLateralX = startLateral.X.ToDouble();
        var startLateralY = startLateral.Y.ToDouble();
        var endLateralX = endLateral.X.ToDouble();
        var endLateralY = endLateral.Y.ToDouble();

        // Cross-section coordinate -1 is center + RightOrthogonalVector,
        // while coordinate +1 is center - RightOrthogonalVector.
        var startRight = new RoadSurfaceVertex(
            startX + startLateralX * laneHalfWidthTiles,
            startY + startLateralY * laneHalfWidthTiles,
            startZ);
        var startLeft = new RoadSurfaceVertex(
            startX - startLateralX * laneHalfWidthTiles,
            startY - startLateralY * laneHalfWidthTiles,
            startZ);
        var endLeft = new RoadSurfaceVertex(
            endX - endLateralX * laneHalfWidthTiles,
            endY - endLateralY * laneHalfWidthTiles,
            endZ);
        var endRight = new RoadSurfaceVertex(
            endX + endLateralX * laneHalfWidthTiles,
            endY + endLateralY * laneHalfWidthTiles,
            endZ);

        // MeshBuilder's shared top-face diagonal is startRight -> endLeft.
        // Mirroring its winding matters for curved, inclined, non-planar
        // quads because the two possible diagonals define different Z.
        return (
            startRight,
            endLeft,
            endRight,
            endLeft,
            startRight,
            startLeft);
    }

    private bool DoesRoadSurfaceTriangleMatchTerrain(
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        bool allowAirBelow)
    {
        if (!TryCreateRoadSurfacePlane(
                first,
                second,
                third,
                out var roadPlane))
        {
            return false;
        }

        var minCellX = (int)Math.Floor(
            Math.Min(first.X, Math.Min(second.X, third.X)));
        var maxCellX = (int)Math.Floor(
            Math.Max(first.X, Math.Max(second.X, third.X)));
        var minCellY = (int)Math.Floor(
            Math.Min(first.Y, Math.Min(second.Y, third.Y)));
        var maxCellY = (int)Math.Floor(
            Math.Max(first.Y, Math.Max(second.Y, third.Y)));
        var foundCell = false;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        {
            for (var cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                var cell = new Tile2i(cellX, cellY);
                if (!DoesRoadSurfaceTriangleOverlapTerrainCell(
                        first,
                        second,
                        third,
                        cell))
                {
                    continue;
                }

                foundCell = true;
                if (!DoesTerrainCellMatchRoadTriangle(
                        first,
                        second,
                        third,
                        roadPlane,
                        cell,
                        allowAirBelow))
                {
                    return false;
                }
            }
        }

        return foundCell;
    }

    private bool DoesTerrainCellMatchRoadTriangle(
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        RoadSurfacePlane roadPlane,
        Tile2i cell,
        bool allowAirBelow)
    {
        var bottomLeft = cell;
        var bottomRight = cell + new RelTile2i(1, 0);
        var topLeft = cell + new RelTile2i(0, 1);
        var topRight = cell + new RelTile2i(1, 1);
        if (!m_terrainManager.IsValidCoord(bottomLeft) ||
            !m_terrainManager.IsValidCoord(bottomRight) ||
            !m_terrainManager.IsValidCoord(topLeft) ||
            !m_terrainManager.IsValidCoord(topRight) ||
            m_terrainManager.IsOcean(bottomLeft) ||
            m_terrainManager.IsOcean(bottomRight) ||
            m_terrainManager.IsOcean(topLeft) ||
            m_terrainManager.IsOcean(topRight))
        {
            return false;
        }

        var terrain = new BilinearTerrainPatch(
            cell,
            m_terrainManager.GetHeight(bottomLeft),
            m_terrainManager.GetHeight(bottomRight),
            m_terrainManager.GetHeight(topLeft),
            m_terrainManager.GetHeight(topRight));
        return DoesBilinearTerrainCellMatchRoadTriangleCore(
            first,
            second,
            third,
            roadPlane,
            terrain,
            allowAirBelow);
    }

    private static bool DoesBilinearTerrainCellMatchRoadTriangle(
        Tile3f first,
        Tile3f second,
        Tile3f third,
        Tile2i cell,
        HeightTilesF bottomLeft,
        HeightTilesF bottomRight,
        HeightTilesF topLeft,
        HeightTilesF topRight,
        bool allowAirBelow)
    {
        var firstVertex = new RoadSurfaceVertex(first);
        var secondVertex = new RoadSurfaceVertex(second);
        var thirdVertex = new RoadSurfaceVertex(third);
        if (!TryCreateRoadSurfacePlane(
                firstVertex,
                secondVertex,
                thirdVertex,
                out var roadPlane) ||
            !DoesRoadSurfaceTriangleOverlapTerrainCell(
                firstVertex,
                secondVertex,
                thirdVertex,
                cell))
        {
            return true;
        }

        return DoesBilinearTerrainCellMatchRoadTriangleCore(
            firstVertex,
            secondVertex,
            thirdVertex,
            roadPlane,
            new BilinearTerrainPatch(
                cell,
                bottomLeft,
                bottomRight,
                topLeft,
                topRight),
            allowAirBelow);
    }

    private static bool DoesBilinearTerrainCellMatchRoadTriangleCore(
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        RoadSurfacePlane roadPlane,
        BilinearTerrainPatch terrain,
        bool allowAirBelow)
    {
        return DoesClippedTriangleEdgeMatchTerrain(
                   first.Xy,
                   second.Xy,
                   terrain,
                   roadPlane,
                   allowAirBelow) &&
               DoesClippedTriangleEdgeMatchTerrain(
                   second.Xy,
                   third.Xy,
                   terrain,
                   roadPlane,
                   allowAirBelow) &&
               DoesClippedTriangleEdgeMatchTerrain(
                   third.Xy,
                   first.Xy,
                   terrain,
                   roadPlane,
                   allowAirBelow) &&
               DoesClippedCellEdgeMatchTerrain(
                   new SurfacePoint2d(terrain.Cell.X, terrain.Cell.Y),
                   new SurfacePoint2d(terrain.Cell.X + 1.0, terrain.Cell.Y),
                   first,
                   second,
                   third,
                   terrain,
                   roadPlane,
                   allowAirBelow) &&
               DoesClippedCellEdgeMatchTerrain(
                   new SurfacePoint2d(
                       terrain.Cell.X + 1.0,
                       terrain.Cell.Y),
                   new SurfacePoint2d(
                       terrain.Cell.X + 1.0,
                       terrain.Cell.Y + 1.0),
                   first,
                   second,
                   third,
                   terrain,
                   roadPlane,
                   allowAirBelow) &&
               DoesClippedCellEdgeMatchTerrain(
                   new SurfacePoint2d(
                       terrain.Cell.X + 1.0,
                       terrain.Cell.Y + 1.0),
                   new SurfacePoint2d(
                       terrain.Cell.X,
                       terrain.Cell.Y + 1.0),
                   first,
                   second,
                   third,
                   terrain,
                   roadPlane,
                   allowAirBelow) &&
               DoesClippedCellEdgeMatchTerrain(
                   new SurfacePoint2d(
                       terrain.Cell.X,
                       terrain.Cell.Y + 1.0),
                   new SurfacePoint2d(terrain.Cell.X, terrain.Cell.Y),
                   first,
                   second,
                   third,
                   terrain,
                   roadPlane,
                   allowAirBelow);
    }

    private static bool DoesClippedTriangleEdgeMatchTerrain(
        SurfacePoint2d start,
        SurfacePoint2d end,
        BilinearTerrainPatch terrain,
        RoadSurfacePlane roadPlane,
        bool allowAirBelow)
    {
        return !TryClipSegmentToTerrainCell(
                   start,
                   end,
                   terrain.Cell,
                   out var clippedStart,
                   out var clippedEnd) ||
               DoesTerrainRoadDifferenceStayWithinToleranceOnAffineLine(
                   terrain,
                   roadPlane,
                   clippedStart,
                   clippedEnd,
                   allowAirBelow);
    }

    private static bool DoesClippedCellEdgeMatchTerrain(
        SurfacePoint2d start,
        SurfacePoint2d end,
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        BilinearTerrainPatch terrain,
        RoadSurfacePlane roadPlane,
        bool allowAirBelow)
    {
        return !TryClipSegmentToRoadTriangle(
                   start,
                   end,
                   first,
                   second,
                   third,
                   out var clippedStart,
                   out var clippedEnd) ||
               DoesTerrainRoadDifferenceStayWithinToleranceOnAffineLine(
                   terrain,
                   roadPlane,
                   clippedStart,
                   clippedEnd,
                   allowAirBelow);
    }

    private static bool DoesTerrainRoadDifferenceStayWithinToleranceOnAffineLine(
        BilinearTerrainPatch terrain,
        RoadSurfacePlane roadPlane,
        SurfacePoint2d lineStart,
        SurfacePoint2d lineEnd,
        bool allowAirBelow)
    {
        var differenceStart = terrain.GetHeight(lineStart) -
            roadPlane.GetHeight(lineStart);
        var differenceMiddle = terrain.GetHeight(
                lineStart.Lerp(lineEnd, 0.5)) -
            roadPlane.GetHeight(lineStart.Lerp(lineEnd, 0.5));
        var differenceEnd = terrain.GetHeight(lineEnd) -
            roadPlane.GetHeight(lineEnd);
        if (!IsTerrainRoadHeightDifferenceWithinTolerance(
                differenceStart,
                allowAirBelow) ||
            !IsTerrainRoadHeightDifferenceWithinTolerance(
                differenceMiddle,
                allowAirBelow) ||
            !IsTerrainRoadHeightDifferenceWithinTolerance(
                differenceEnd,
                allowAirBelow))
        {
            return false;
        }

        var quadratic = 2.0 *
            (differenceStart + differenceEnd - 2.0 * differenceMiddle);
        if (Math.Abs(quadratic) <= 1e-12)
        {
            return true;
        }

        var linear = differenceEnd - differenceStart - quadratic;
        var stationaryProgress = -linear / (2.0 * quadratic);
        if (stationaryProgress <= 0.0 || stationaryProgress >= 1.0)
        {
            return true;
        }

        var stationaryPoint = lineStart.Lerp(lineEnd, stationaryProgress);
        return IsTerrainRoadHeightDifferenceWithinTolerance(
            terrain.GetHeight(stationaryPoint) -
            roadPlane.GetHeight(stationaryPoint),
            allowAirBelow);
    }

    private static bool TryCreateRoadSurfacePlane(
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        out RoadSurfacePlane result)
    {
        var firstSecondX = second.X - first.X;
        var firstSecondY = second.Y - first.Y;
        var firstThirdX = third.X - first.X;
        var firstThirdY = third.Y - first.Y;
        var determinant = firstSecondX * firstThirdY -
            firstSecondY * firstThirdX;
        if (Math.Abs(determinant) <= 1e-10)
        {
            result = default;
            return false;
        }

        var firstSecondZ = second.Z - first.Z;
        var firstThirdZ = third.Z - first.Z;
        var xFactor = (firstSecondZ * firstThirdY -
                       firstThirdZ * firstSecondY) / determinant;
        var yFactor = (firstSecondX * firstThirdZ -
                       firstThirdX * firstSecondZ) / determinant;
        result = new RoadSurfacePlane(
            xFactor,
            yFactor,
            first.Z - xFactor * first.X - yFactor * first.Y);
        return true;
    }

    private static bool DoesRoadSurfaceTriangleOverlapTerrainCell(
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        Tile2i cell)
    {
        return HasPositiveProjectionOverlap(
                   first,
                   second,
                   third,
                   cell,
                   1.0,
                   0.0) &&
               HasPositiveProjectionOverlap(
                   first,
                   second,
                   third,
                   cell,
                   0.0,
                   1.0) &&
               HasPositiveProjectionOverlap(
                   first,
                   second,
                   third,
                   cell,
                   -(second.Y - first.Y),
                   second.X - first.X) &&
               HasPositiveProjectionOverlap(
                   first,
                   second,
                   third,
                   cell,
                   -(third.Y - second.Y),
                   third.X - second.X) &&
               HasPositiveProjectionOverlap(
                   first,
                   second,
                   third,
                   cell,
                   -(first.Y - third.Y),
                   first.X - third.X);
    }

    private static bool HasPositiveProjectionOverlap(
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        Tile2i cell,
        double axisX,
        double axisY)
    {
        var axisLengthSquared = axisX * axisX + axisY * axisY;
        if (axisLengthSquared <= 1e-20)
        {
            return true;
        }

        var firstProjection = first.X * axisX + first.Y * axisY;
        var secondProjection = second.X * axisX + second.Y * axisY;
        var thirdProjection = third.X * axisX + third.Y * axisY;
        var triangleMinimum = Math.Min(
            firstProjection,
            Math.Min(secondProjection, thirdProjection));
        var triangleMaximum = Math.Max(
            firstProjection,
            Math.Max(secondProjection, thirdProjection));
        var cell00 = cell.X * axisX + cell.Y * axisY;
        var cell10 = (cell.X + 1.0) * axisX + cell.Y * axisY;
        var cell01 = cell.X * axisX + (cell.Y + 1.0) * axisY;
        var cell11 = (cell.X + 1.0) * axisX +
            (cell.Y + 1.0) * axisY;
        var cellMinimum = Math.Min(
            Math.Min(cell00, cell10),
            Math.Min(cell01, cell11));
        var cellMaximum = Math.Max(
            Math.Max(cell00, cell10),
            Math.Max(cell01, cell11));
        var overlap = Math.Min(triangleMaximum, cellMaximum) -
            Math.Max(triangleMinimum, cellMinimum);
        return overlap > 1e-10 * Math.Sqrt(axisLengthSquared);
    }

    private static bool TryClipSegmentToTerrainCell(
        SurfacePoint2d start,
        SurfacePoint2d end,
        Tile2i cell,
        out SurfacePoint2d clippedStart,
        out SurfacePoint2d clippedEnd)
    {
        var minimum = 0.0;
        var maximum = 1.0;
        if (!ClipSegmentToAxisRange(
                start.X,
                end.X - start.X,
                cell.X,
                cell.X + 1.0,
                ref minimum,
                ref maximum) ||
            !ClipSegmentToAxisRange(
                start.Y,
                end.Y - start.Y,
                cell.Y,
                cell.Y + 1.0,
                ref minimum,
                ref maximum))
        {
            clippedStart = default;
            clippedEnd = default;
            return false;
        }

        clippedStart = start.Lerp(end, minimum);
        clippedEnd = start.Lerp(end, maximum);
        return true;
    }

    private static bool ClipSegmentToAxisRange(
        double origin,
        double direction,
        double axisMinimum,
        double axisMaximum,
        ref double segmentMinimum,
        ref double segmentMaximum)
    {
        if (Math.Abs(direction) <= 1e-12)
        {
            return origin >= axisMinimum - 1e-10 &&
                   origin <= axisMaximum + 1e-10;
        }

        var first = (axisMinimum - origin) / direction;
        var second = (axisMaximum - origin) / direction;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        segmentMinimum = Math.Max(segmentMinimum, first);
        segmentMaximum = Math.Min(segmentMaximum, second);
        return segmentMinimum <= segmentMaximum + 1e-10;
    }

    private static bool TryClipSegmentToRoadTriangle(
        SurfacePoint2d start,
        SurfacePoint2d end,
        RoadSurfaceVertex first,
        RoadSurfaceVertex second,
        RoadSurfaceVertex third,
        out SurfacePoint2d clippedStart,
        out SurfacePoint2d clippedEnd)
    {
        var orientation = Cross2d(first.Xy, second.Xy, third.Xy);
        if (Math.Abs(orientation) <= 1e-10)
        {
            clippedStart = default;
            clippedEnd = default;
            return false;
        }

        var orientationSign = orientation > 0.0 ? 1.0 : -1.0;
        var minimum = 0.0;
        var maximum = 1.0;
        if (!ClipSegmentToTriangleEdge(
                start,
                end,
                first.Xy,
                second.Xy,
                orientationSign,
                ref minimum,
                ref maximum) ||
            !ClipSegmentToTriangleEdge(
                start,
                end,
                second.Xy,
                third.Xy,
                orientationSign,
                ref minimum,
                ref maximum) ||
            !ClipSegmentToTriangleEdge(
                start,
                end,
                third.Xy,
                first.Xy,
                orientationSign,
                ref minimum,
                ref maximum))
        {
            clippedStart = default;
            clippedEnd = default;
            return false;
        }

        clippedStart = start.Lerp(end, minimum);
        clippedEnd = start.Lerp(end, maximum);
        return true;
    }

    private static bool ClipSegmentToTriangleEdge(
        SurfacePoint2d start,
        SurfacePoint2d end,
        SurfacePoint2d edgeStart,
        SurfacePoint2d edgeEnd,
        double orientationSign,
        ref double segmentMinimum,
        ref double segmentMaximum)
    {
        var startDistance = orientationSign *
            Cross2d(edgeStart, edgeEnd, start);
        var endDistance = orientationSign *
            Cross2d(edgeStart, edgeEnd, end);
        if (startDistance < -1e-10 && endDistance < -1e-10)
        {
            return false;
        }

        if (startDistance >= -1e-10 && endDistance >= -1e-10)
        {
            return true;
        }

        var intersection = startDistance /
            (startDistance - endDistance);
        if (startDistance < -1e-10)
        {
            segmentMinimum = Math.Max(segmentMinimum, intersection);
        }
        else
        {
            segmentMaximum = Math.Min(segmentMaximum, intersection);
        }

        return segmentMinimum <= segmentMaximum + 1e-10;
    }

    private static double Cross2d(
        SurfacePoint2d first,
        SurfacePoint2d second,
        SurfacePoint2d third)
    {
        return (second.X - first.X) * (third.Y - first.Y) -
            (second.Y - first.Y) * (third.X - first.X);
    }

    private static bool IsTerrainRoadHeightDifferenceWithinTolerance(
        double difference,
        bool allowAirBelow)
    {
        // Highway supports make dry air below either a flat or an inclined
        // deck valid. Terrain above the thin asphalt surface is a different
        // case: keep it below the visible top so the crest cannot disappear
        // into a dumped plateau.
        var maximumSupportDepth = allowAirBelow
            ? TrainTrackPillarProto.MAX_PILLAR_HEIGHT.Value
            : GroundSupportToleranceTiles;
        return difference <= TerrainPenetrationToleranceTiles + 1e-8 &&
               difference >= -maximumSupportDepth - 1e-8;
    }

    private bool HasRoadSurfaceEntityClearance(TrainTrackPlan plan)
    {
        if (m_pieces.Count == 0 ||
            m_builtPrefixStepCount < 0 ||
            m_builtPrefixStepCount > plan.Steps.Length)
        {
            return false;
        }

        GetLogicalRoadPorts(
            m_pieces[0],
            out var plannedStartPort,
            out _);
        GetLogicalRoadPorts(
            m_pieces[m_pieces.Count - 1],
            out _,
            out var plannedEndPort);

        var checkedRanges =
            new HashSet<(
                int X,
                int Y,
                int From,
                int Size,
                bool Start,
                bool End)>();
        for (var stepIndex = m_builtPrefixStepCount;
             stepIndex < plan.Steps.Length;
             stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            var allowsStartAttachment =
                stepIndex == m_builtPrefixStepCount;
            var allowsEndAttachment =
                stepIndex == plan.Steps.Length - 1;
            var occupiedCoords = new HashSet<RelTile2i>();
            foreach (var occupied in step.OccupiedTilesRelative)
            {
                occupiedCoords.Add(occupied.RelCoord);
            }

            // The native track layout is roughly two tiles wide. Recheck that
            // original layout against the current world, then validate the
            // profile-specific fringe out to the full paved half-width.
            // Exact endpoint entities are permitted only inside their small
            // seam area below.
            foreach (var occupied in step.OccupiedTilesRelative)
            {
                var occupiedFrom = step.Transform.Position.Height +
                    occupied.FromHeightRel;
                if (!IsRoadSurfaceOccupancyRangeClear(
                        step.Transform.Position.Xy + occupied.RelCoord,
                        occupiedFrom,
                        occupied.VerticalSize,
                        checkedRanges,
                        plannedStartPort,
                        plannedEndPort,
                        allowsStartAttachment,
                        allowsEndAttachment))
                {
                    return false;
                }

                var occupancyFringe = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        HighwayRoadProfile.Get(m_selectedTier)
                            .RoadHalfWidthTiles - 1.0));
                for (var deltaX = -occupancyFringe;
                     deltaX <= occupancyFringe;
                     deltaX++)
                {
                    for (var deltaY = -occupancyFringe;
                         deltaY <= occupancyFringe;
                         deltaY++)
                    {
                        var candidate = occupied.RelCoord +
                            new RelTile2i(deltaX, deltaY);
                        if (occupiedCoords.Contains(candidate))
                        {
                            continue;
                        }

                        var worldTile =
                            step.Transform.Position.Xy + candidate;
                        if (!IsRoadSurfaceOccupancyRangeClear(
                                worldTile,
                                occupiedFrom,
                                occupied.VerticalSize,
                                checkedRanges,
                                plannedStartPort,
                                plannedEndPort,
                                allowsStartAttachment,
                                allowsEndAttachment))
                        {
                            return false;
                        }
                    }
                }
            }
        }

        return HasExistingHighwaySurfaceClearance(
            plannedStartPort,
            plannedEndPort);
    }

    private bool HasExistingHighwaySurfaceClearance(
        HighwayPort plannedStartPort,
        HighwayPort plannedEndPort)
    {
        var existingSegmentsByBucket =
            new Dictionary<(int X, int Y), List<RoadSurfaceSegment>>();
        var allowedStartAttachmentPorts =
            new Dictionary<EntityId, List<HighwayPort>>();
        var allowedEndAttachmentPorts =
            new Dictionary<EntityId, List<HighwayPort>>();
        var existingSegments = new List<RoadSurfaceSegment>();

        foreach (var entity in
                 m_entitiesManager.GetAllEntitiesOfType<RoadEntityBase>())
        {
            if (entity.IsDestroyed ||
                entity.RoadProto is not IHighwayMainlineProto mainlineProto ||
                !mainlineProto.ParticipatesInHighwayNetwork)
            {
                continue;
            }

            CollectMatchingAttachmentCenters(
                entity,
                plannedStartPort,
                plannedEndPort,
                allowedStartAttachmentPorts,
                allowedEndAttachmentPorts);
            AppendExistingRoadSurfaceSegments(entity, existingSegments);
        }

        foreach (var segment in existingSegments)
        {
            AddRoadSurfaceSegmentToBuckets(
                segment,
                existingSegmentsByBucket);
        }

        if (existingSegmentsByBucket.Count == 0)
        {
            return true;
        }

        var plannedSegments = new List<RoadSurfaceSegment>();
        for (var pieceIndex = 0;
             pieceIndex < m_pieces.Count;
             pieceIndex++)
        {
            AppendPlannedRoadSurfaceSegments(
                m_pieces[pieceIndex],
                allowsStartAttachment: pieceIndex == 0,
                allowsEndAttachment: pieceIndex == m_pieces.Count - 1,
                result: plannedSegments);
        }

        foreach (var planned in plannedSegments)
        {
            var minBucketX = GetRoadSurfaceBucket(
                Math.Min(planned.StartX, planned.EndX) -
                MaximumRoadSurfaceCollisionDistance);
            var maxBucketX = GetRoadSurfaceBucket(
                Math.Max(planned.StartX, planned.EndX) +
                MaximumRoadSurfaceCollisionDistance);
            var minBucketY = GetRoadSurfaceBucket(
                Math.Min(planned.StartY, planned.EndY) -
                MaximumRoadSurfaceCollisionDistance);
            var maxBucketY = GetRoadSurfaceBucket(
                Math.Max(planned.StartY, planned.EndY) +
                MaximumRoadSurfaceCollisionDistance);
            for (var bucketX = minBucketX;
                 bucketX <= maxBucketX;
                 bucketX++)
            {
                for (var bucketY = minBucketY;
                     bucketY <= maxBucketY;
                     bucketY++)
                {
                    if (!existingSegmentsByBucket.TryGetValue(
                            (bucketX, bucketY),
                            out var candidates))
                    {
                        continue;
                    }

                    foreach (var existing in candidates)
                    {
                        if (!DoRoadSurfaceSegmentsOverlap(
                                planned,
                                existing) ||
                            IsAllowedGeometricAttachmentOverlap(
                                planned,
                                existing.EntityId,
                                allowedStartAttachmentPorts,
                                allowedEndAttachmentPorts))
                        {
                            continue;
                        }

                        return false;
                    }
                }
            }
        }

        return true;
    }

    private void CollectMatchingAttachmentCenters(
        RoadEntityBase entity,
        HighwayPort plannedStartPort,
        HighwayPort plannedEndPort,
        Dictionary<EntityId, List<HighwayPort>> startResult,
        Dictionary<EntityId, List<HighwayPort>> endResult)
    {
        if (entity.RoadProto is not IHighwayPortProto portProto)
        {
            return;
        }

        for (var portIndex = 0;
             portIndex < portProto.HighwayPortCount;
             portIndex++)
        {
            var existingPort = portProto.GetHighwayPort(
                portIndex,
                entity.Transform);
            var isStart = IsAuthorizedAttachmentPort(
                existingPort,
                plannedStartPort,
                plannedEndPort,
                allowsStartAttachment: true,
                allowsEndAttachment: false);
            var isEnd = IsAuthorizedAttachmentPort(
                existingPort,
                plannedStartPort,
                plannedEndPort,
                allowsStartAttachment: false,
                allowsEndAttachment: true);
            if (!isStart && !isEnd)
            {
                continue;
            }

            if (isStart)
            {
                AddAttachmentPort(
                    entity.Id,
                    existingPort,
                    startResult);
            }

            if (isEnd)
            {
                AddAttachmentPort(
                    entity.Id,
                    existingPort,
                    endResult);
            }
        }
    }

    private static void AddAttachmentPort(
        EntityId entityId,
        HighwayPort port,
        Dictionary<EntityId, List<HighwayPort>> result)
    {
        if (!result.TryGetValue(entityId, out var ports))
        {
            ports = new List<HighwayPort>();
            result.Add(entityId, ports);
        }

        if (!ports.Any(existing => AreSamePort(existing, port)))
        {
            ports.Add(port);
        }
    }

    private static void AppendExistingRoadSurfaceSegments(
        RoadEntityBase entity,
        List<RoadSurfaceSegment> result)
    {
        var origin = entity.CenterTile.CornerTile3f;
        var laneHalfWidth =
            entity.RoadProto is IHighwayLaneProfileProto profile
                ? profile.VisualLaneWidthTiles / 2.0
                : 1.0;
        for (var laneIndex = 0;
             laneIndex < entity.RoadLanesCount;
             laneIndex++)
        {
            var lane = entity.GetTransformedRoadLane(laneIndex);
            for (var sampleIndex = 1;
                 sampleIndex < lane.LaneCenterSamples.Length;
                 sampleIndex++)
            {
                AppendRoadSurfaceSegments(
                    origin + lane.LaneCenterSamples[sampleIndex - 1],
                    origin + lane.LaneCenterSamples[sampleIndex],
                    entity.Id,
                    allowsStartAttachment: false,
                    allowsEndAttachment: false,
                    laneHalfWidth,
                    result: result);
            }
        }
    }

    private static void AppendPlannedRoadSurfaceSegments(
        Piece piece,
        bool allowsStartAttachment,
        bool allowsEndAttachment,
        List<RoadSurfaceSegment> result)
    {
        for (var laneIndex = 0;
             laneIndex < piece.Proto.LanesTrajectories.Length;
             laneIndex++)
        {
            var lane = piece.Proto.LanesTrajectories[laneIndex];
            for (var sampleIndex = 1;
                 sampleIndex < lane.LaneCenterSamples.Length;
                 sampleIndex++)
            {
                AppendRoadSurfaceSegments(
                    piece.Proto.Layout.TransformPoint_RelToCenterTile(
                        lane.LaneCenterSamples[sampleIndex - 1],
                        piece.Transform),
                    piece.Proto.Layout.TransformPoint_RelToCenterTile(
                        lane.LaneCenterSamples[sampleIndex],
                    piece.Transform),
                    EntityId.Invalid,
                    allowsStartAttachment,
                    allowsEndAttachment,
                    piece.Proto.VisualLaneWidthTiles / 2.0,
                    result);
            }
        }
    }

    private static void AppendRoadSurfaceSegments(
        Tile3f start,
        Tile3f end,
        EntityId entityId,
        bool allowsStartAttachment,
        bool allowsEndAttachment,
        double laneHalfWidth,
        List<RoadSurfaceSegment> result)
    {
        var startX = start.X.ToDouble();
        var startY = start.Y.ToDouble();
        var startZ = start.Z.ToDouble();
        var endX = end.X.ToDouble();
        var endY = end.Y.ToDouble();
        var endZ = end.Z.ToDouble();
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var deltaZ = endZ - startZ;
        var length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        var stepCount = Math.Max(
            1,
            (int)Math.Ceiling(length / RoadSurfaceSampleSpacing));
        for (var step = 0; step < stepCount; step++)
        {
            var startT = (double)step / stepCount;
            var endT = (double)(step + 1) / stepCount;
            result.Add(new RoadSurfaceSegment(
                startX + deltaX * startT,
                startY + deltaY * startT,
                startZ + deltaZ * startT,
                startX + deltaX * endT,
                startY + deltaY * endT,
                startZ + deltaZ * endT,
                    entityId,
                    allowsStartAttachment,
                    allowsEndAttachment,
                    laneHalfWidth));
        }
    }

    private static void AddRoadSurfaceSegmentToBuckets(
        RoadSurfaceSegment segment,
        Dictionary<(int X, int Y), List<RoadSurfaceSegment>> buckets)
    {
        var minBucketX = GetRoadSurfaceBucket(
            Math.Min(segment.StartX, segment.EndX));
        var maxBucketX = GetRoadSurfaceBucket(
            Math.Max(segment.StartX, segment.EndX));
        var minBucketY = GetRoadSurfaceBucket(
            Math.Min(segment.StartY, segment.EndY));
        var maxBucketY = GetRoadSurfaceBucket(
            Math.Max(segment.StartY, segment.EndY));
        for (var bucketX = minBucketX;
             bucketX <= maxBucketX;
             bucketX++)
        {
            for (var bucketY = minBucketY;
                 bucketY <= maxBucketY;
                 bucketY++)
            {
                var key = (bucketX, bucketY);
                if (!buckets.TryGetValue(key, out var segments))
                {
                    segments = new List<RoadSurfaceSegment>();
                    buckets.Add(key, segments);
                }

                segments.Add(segment);
            }
        }
    }

    private static int GetRoadSurfaceBucket(double coordinate)
    {
        return (int)Math.Floor(
            coordinate / RoadSurfaceCollisionBucketSize);
    }

    private static bool IsAllowedGeometricAttachmentOverlap(
        RoadSurfaceSegment planned,
        EntityId existingEntityId,
        Dictionary<EntityId, List<HighwayPort>> allowedStartAttachmentPorts,
        Dictionary<EntityId, List<HighwayPort>> allowedEndAttachmentPorts)
    {
        return planned.AllowsStartAttachment &&
                   IsGeometricAttachmentOverlapWithinPorts(
                       planned,
                       existingEntityId,
                       allowedStartAttachmentPorts) ||
               planned.AllowsEndAttachment &&
                   IsGeometricAttachmentOverlapWithinPorts(
                       planned,
                       existingEntityId,
                       allowedEndAttachmentPorts);
    }

    private static bool IsGeometricAttachmentOverlapWithinPorts(
        RoadSurfaceSegment planned,
        EntityId existingEntityId,
        Dictionary<EntityId, List<HighwayPort>> allowedAttachmentPorts)
    {
        if (allowedAttachmentPorts.TryGetValue(
                existingEntityId,
                out var ports))
        {
            foreach (var port in ports)
            {
                if (IsRoadSurfacePointWithinAttachmentSeam(
                        planned.StartX,
                        planned.StartY,
                        port.Center.Xy,
                        port.AttachmentCollisionSeamRange) &&
                    IsRoadSurfacePointWithinAttachmentSeam(
                        planned.EndX,
                        planned.EndY,
                        port.Center.Xy,
                        port.AttachmentCollisionSeamRange))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRoadSurfacePointWithinAttachmentSeam(
        double x,
        double y,
        Tile2i center,
        int seamRange)
    {
        var tolerance = seamRange +
                        RoadSurfaceSampleSpacing;
        return Math.Abs(x - center.X) <= tolerance &&
               Math.Abs(y - center.Y) <= tolerance;
    }

    private static bool DoRoadSurfaceSegmentsOverlap(
        RoadSurfaceSegment first,
        RoadSurfaceSegment second)
    {
        var distanceSquared = SegmentDistanceSquared2d(
            first.StartX,
            first.StartY,
            first.EndX,
            first.EndY,
            second.StartX,
            second.StartY,
            second.EndX,
            second.EndY);
        var collisionDistance = first.HalfWidth + second.HalfWidth;
        if (distanceSquared >= collisionDistance * collisionDistance)
        {
            return false;
        }

        // These are terrain roads, not bridges. Any non-port-bound XY
        // overlap is rejected regardless of Z so a ramp cannot become an
        // unsafe low-clearance overpass.
        return true;
    }

    private static bool DoRoadSurfaceLinesOverlap(
        Tile3f firstStart,
        Tile3f firstEnd,
        Tile3f secondStart,
        Tile3f secondEnd)
    {
        var first = new RoadSurfaceSegment(
            firstStart.X.ToDouble(),
            firstStart.Y.ToDouble(),
            firstStart.Z.ToDouble(),
            firstEnd.X.ToDouble(),
            firstEnd.Y.ToDouble(),
            firstEnd.Z.ToDouble(),
            EntityId.Invalid,
            allowsStartAttachment: false,
            allowsEndAttachment: false);
        var second = new RoadSurfaceSegment(
            secondStart.X.ToDouble(),
            secondStart.Y.ToDouble(),
            secondStart.Z.ToDouble(),
            secondEnd.X.ToDouble(),
            secondEnd.Y.ToDouble(),
            secondEnd.Z.ToDouble(),
            EntityId.Invalid,
            allowsStartAttachment: false,
            allowsEndAttachment: false);
        return DoRoadSurfaceSegmentsOverlap(first, second);
    }

    private static double SegmentDistanceSquared2d(
        double firstStartX,
        double firstStartY,
        double firstEndX,
        double firstEndY,
        double secondStartX,
        double secondStartY,
        double secondEndX,
        double secondEndY)
    {
        if (SegmentsIntersect2d(
                firstStartX,
                firstStartY,
                firstEndX,
                firstEndY,
                secondStartX,
                secondStartY,
                secondEndX,
                secondEndY))
        {
            return 0.0;
        }

        return Math.Min(
            Math.Min(
                PointToSegmentDistanceSquared2d(
                    firstStartX,
                    firstStartY,
                    secondStartX,
                    secondStartY,
                    secondEndX,
                    secondEndY),
                PointToSegmentDistanceSquared2d(
                    firstEndX,
                    firstEndY,
                    secondStartX,
                    secondStartY,
                    secondEndX,
                    secondEndY)),
            Math.Min(
                PointToSegmentDistanceSquared2d(
                    secondStartX,
                    secondStartY,
                    firstStartX,
                    firstStartY,
                    firstEndX,
                    firstEndY),
                PointToSegmentDistanceSquared2d(
                    secondEndX,
                    secondEndY,
                    firstStartX,
                    firstStartY,
                    firstEndX,
                    firstEndY)));
    }

    private static bool SegmentsIntersect2d(
        double firstStartX,
        double firstStartY,
        double firstEndX,
        double firstEndY,
        double secondStartX,
        double secondStartY,
        double secondEndX,
        double secondEndY)
    {
        const double epsilon = 1e-9;
        var firstSideStart = Cross2d(
            firstStartX,
            firstStartY,
            firstEndX,
            firstEndY,
            secondStartX,
            secondStartY);
        var firstSideEnd = Cross2d(
            firstStartX,
            firstStartY,
            firstEndX,
            firstEndY,
            secondEndX,
            secondEndY);
        var secondSideStart = Cross2d(
            secondStartX,
            secondStartY,
            secondEndX,
            secondEndY,
            firstStartX,
            firstStartY);
        var secondSideEnd = Cross2d(
            secondStartX,
            secondStartY,
            secondEndX,
            secondEndY,
            firstEndX,
            firstEndY);
        if (((firstSideStart > epsilon && firstSideEnd < -epsilon) ||
             (firstSideStart < -epsilon && firstSideEnd > epsilon)) &&
            ((secondSideStart > epsilon && secondSideEnd < -epsilon) ||
             (secondSideStart < -epsilon && secondSideEnd > epsilon)))
        {
            return true;
        }

        return Math.Abs(firstSideStart) <= epsilon &&
                   IsPointOnSegment2d(
                       secondStartX,
                       secondStartY,
                       firstStartX,
                       firstStartY,
                       firstEndX,
                       firstEndY,
                       epsilon) ||
               Math.Abs(firstSideEnd) <= epsilon &&
                   IsPointOnSegment2d(
                       secondEndX,
                       secondEndY,
                       firstStartX,
                       firstStartY,
                       firstEndX,
                       firstEndY,
                       epsilon) ||
               Math.Abs(secondSideStart) <= epsilon &&
                   IsPointOnSegment2d(
                       firstStartX,
                       firstStartY,
                       secondStartX,
                       secondStartY,
                       secondEndX,
                       secondEndY,
                       epsilon) ||
               Math.Abs(secondSideEnd) <= epsilon &&
                   IsPointOnSegment2d(
                       firstEndX,
                       firstEndY,
                       secondStartX,
                       secondStartY,
                       secondEndX,
                       secondEndY,
                       epsilon);
    }

    private static double Cross2d(
        double startX,
        double startY,
        double endX,
        double endY,
        double pointX,
        double pointY)
    {
        return (endX - startX) * (pointY - startY) -
               (endY - startY) * (pointX - startX);
    }

    private static bool IsPointOnSegment2d(
        double pointX,
        double pointY,
        double startX,
        double startY,
        double endX,
        double endY,
        double epsilon)
    {
        return pointX >= Math.Min(startX, endX) - epsilon &&
               pointX <= Math.Max(startX, endX) + epsilon &&
               pointY >= Math.Min(startY, endY) - epsilon &&
               pointY <= Math.Max(startY, endY) + epsilon;
    }

    private static double PointToSegmentDistanceSquared2d(
        double pointX,
        double pointY,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= 1e-12)
        {
            var pointDeltaX = pointX - startX;
            var pointDeltaY = pointY - startY;
            return pointDeltaX * pointDeltaX +
                   pointDeltaY * pointDeltaY;
        }

        var progress = ((pointX - startX) * deltaX +
                        (pointY - startY) * deltaY) /
                       lengthSquared;
        progress = Math.Max(0.0, Math.Min(1.0, progress));
        var closestX = startX + progress * deltaX;
        var closestY = startY + progress * deltaY;
        var distanceX = pointX - closestX;
        var distanceY = pointY - closestY;
        return distanceX * distanceX + distanceY * distanceY;
    }

    private bool IsRoadSurfaceOccupancyRangeClear(
        Tile2i worldTile,
        HeightTilesI from,
        ThicknessTilesI verticalSize,
        HashSet<(
            int X,
            int Y,
            int From,
            int Size,
            bool Start,
            bool End)> checkedRanges,
        HighwayPort plannedStartPort,
        HighwayPort plannedEndPort,
        bool allowsStartAttachment,
        bool allowsEndAttachment)
    {
        var rangeKey = (
            worldTile.X,
            worldTile.Y,
            from.Value,
            verticalSize.Value,
            allowsStartAttachment,
            allowsEndAttachment);
        if (!checkedRanges.Add(rangeKey))
        {
            return true;
        }

        if (!m_terrainManager.IsValidCoord(worldTile))
        {
            return false;
        }

        var tileIndex = m_terrainManager.GetTileIndex(worldTile);
        if (m_terrainManager.IsOffLimits(tileIndex) ||
            m_terrainManager.IsBlockingBuildings(tileIndex))
        {
            return false;
        }

        m_roadSurfaceCollisionIds.Clear();
        m_terrainOccupancyManager.GetAllOccupyingEntitiesInRange(
            tileIndex,
            from,
            verticalSize,
            m_roadSurfaceCollisionIds);
        foreach (var collisionId in m_roadSurfaceCollisionIds)
        {
            if (!IsAllowedAttachmentCollision(
                    collisionId,
                    worldTile,
                    plannedStartPort,
                    plannedEndPort,
                    allowsStartAttachment,
                    allowsEndAttachment))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsAllowedAttachmentCollision(
        EntityId collisionId,
        Tile2i collisionTile,
        HighwayPort plannedStartPort,
        HighwayPort plannedEndPort,
        bool allowsStartAttachment,
        bool allowsEndAttachment)
    {
        if (!m_entitiesManager.TryGetEntity<RoadEntityBase>(
                collisionId,
                out var entity) ||
            entity.IsDestroyed ||
            entity.RoadProto is not IHighwayPortProto portProto)
        {
            return false;
        }

        for (var portIndex = 0;
             portIndex < portProto.HighwayPortCount;
             portIndex++)
        {
            var existingPort = portProto.GetHighwayPort(
                portIndex,
                entity.Transform);
            if (IsWithinAttachmentCollisionSeam(
                    collisionTile,
                    existingPort.Center.Xy,
                    existingPort.AttachmentCollisionSeamRange) &&
                IsAuthorizedAttachmentPort(
                    existingPort,
                    plannedStartPort,
                    plannedEndPort,
                    allowsStartAttachment,
                    allowsEndAttachment))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAuthorizedAttachmentPort(
        HighwayPort existingPort,
        HighwayPort plannedStartPort,
        HighwayPort plannedEndPort,
        bool allowsStartAttachment,
        bool allowsEndAttachment)
    {
        var isAuthorizedStart = CanUseAttachmentException(
            allowsStartAttachment,
            m_startAttachmentPortIsOpen,
            AreSamePort(existingPort, m_startAttachmentPort),
            plannedStartPort.IsExactMateOf(existingPort));
        var isAuthorizedEnd = CanUseAttachmentException(
            allowsEndAttachment,
            IsGoalAttachmentPortOpen(),
            AreSamePort(existingPort, m_searchGoalPort),
            plannedEndPort.IsExactMateOf(existingPort));
        return isAuthorizedStart || isAuthorizedEnd;
    }

    private static bool CanUseAttachmentException(
        bool isEndpointPiece,
        bool authorizedPortIsOpen,
        bool isSelectedPort,
        bool isExactMate)
    {
        return isEndpointPiece &&
               authorizedPortIsOpen &&
               isSelectedPort &&
               isExactMate;
    }

    private static bool IsWithinAttachmentCollisionSeam(
        Tile2i collisionTile,
        Tile2i portCenter,
        int seamRange)
    {
        return collisionTile.IsNear(
            portCenter,
            seamRange);
    }

    private static void GetLogicalRoadPorts(
        Piece piece,
        out HighwayPort startPort,
        out HighwayPort endPort)
    {
        var isBackward = piece.TrackDirection ==
                         TrainTrackTrajectoryDirection.Backward;
        startPort = piece.Proto.GetHighwayPort(
            isBackward ? 1 : 0,
            piece.Transform);
        endPort = piece.Proto.GetHighwayPort(
            isBackward ? 0 : 1,
            piece.Transform);
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
            rawGoal = m_terrainCursor.Tile3f;
            endDirection = null;
            goalPort = null;
        }

        return m_trackPathFinder.GetGoalFromTile(
            rawGoal,
            m_anchor,
            CreateOptions(endDirection, rawGoal));
    }

    private bool TryGetClosestOpenPort(
        Tile3i cursor,
        out HighwayPort result)
    {
        return TrySelectClosestOpenPort(
            m_openPorts,
            cursor,
            m_hasStart,
            m_anchor,
            out result);
    }

    private static bool IsPortWithinSnapSearchRange(
        HighwayPort port,
        Tile3i cursor)
    {
        return port.Center.Xy.DistanceSqrTo(cursor.Xy) <=
                   GetPortDirectSnapRangeSquared(port) ||
               port.HasExtendedSnapArea &&
               port.SnapAnchor.Xy.IsNear(
                   cursor.Xy,
                   ExtendedPortSnapAnchorRange) ||
               TryGetHighwayEndExtensionSnapScore(
                   port,
                   cursor,
                   out _,
                   out _);
    }

    private static bool TrySelectClosestOpenPort(
        IEnumerable<HighwayPort> ports,
        Tile3i cursor,
        bool hasStart,
        Tile3f anchor,
        out HighwayPort result)
    {
        var found = false;
        var bestPriority = int.MaxValue;
        var bestPrimaryDistance = double.MaxValue;
        var bestSecondaryDistance = double.MaxValue;
        result = default;
        foreach (var port in ports)
        {
            var cursorDistance = port.Center.Xy.DistanceSqrTo(cursor.Xy);
            int priority;
            double primaryDistance;
            double secondaryDistance;
            if (cursorDistance <= GetPortDirectSnapRangeSquared(port))
            {
                // Pointing directly at a physical port always wins.
                priority = 0;
                primaryDistance = cursorDistance;
                secondaryDistance = 0.0;
            }
            else if (port.HasExtendedSnapArea &&
                     port.SnapAnchor.Xy.DistanceSqrTo(cursor.Xy) <=
                     ExtendedPortSnapAnchorMaxDistanceSquared)
            {
                // A click anywhere on a junction body selects a free arm.
                // For an active road, prefer the arm facing its current
                // anchor; before the first pivot, prefer the cursor-nearest
                // arm and retain deterministic discovery order on ties.
                priority = 1;
                primaryDistance = hasStart
                    ? port.Center.Xy.DistanceSqrTo(
                        anchor.Tile3iRounded.Xy)
                    : cursorDistance;
                secondaryDistance = 0.0;
            }
            else if (!hasStart &&
                     TryGetHighwayEndExtensionSnapScore(
                         port,
                         cursor,
                         out var longitudinalDistance,
                         out var lateralDistance))
            {
                // A first click well beyond an ordinary open highway end is
                // interpreted as continuing that highway. Restricting this
                // to its outward corridor keeps both ends of even a one-tile
                // segment unambiguous and avoids grabbing parallel roads.
                priority = 2;
                primaryDistance = lateralDistance;
                secondaryDistance = longitudinalDistance;
            }
            else
            {
                continue;
            }

            if (found &&
                (priority > bestPriority ||
                 priority == bestPriority &&
                 (primaryDistance > bestPrimaryDistance + 1e-8 ||
                  Math.Abs(primaryDistance - bestPrimaryDistance) <= 1e-8 &&
                  secondaryDistance >= bestSecondaryDistance)))
            {
                continue;
            }

            found = true;
            bestPriority = priority;
            bestPrimaryDistance = primaryDistance;
            bestSecondaryDistance = secondaryDistance;
            result = port;
        }

        return found;
    }

    private static bool TryGetHighwayEndExtensionSnapScore(
        HighwayPort port,
        Tile3i cursor,
        out double longitudinalDistance,
        out double lateralDistance)
    {
        longitudinalDistance = 0.0;
        lateralDistance = 0.0;
        if (port.HasExtendedSnapArea)
        {
            return false;
        }

        var outward = port.OutboundNode.Direction.Direction.Vector2f.Normalized;
        if (outward.X.IsZero && outward.Y.IsZero)
        {
            return false;
        }

        var deltaX = cursor.X - port.Center.X;
        var deltaY = cursor.Y - port.Center.Y;
        var outwardX = outward.X.ToDouble();
        var outwardY = outward.Y.ToDouble();
        longitudinalDistance = deltaX * outwardX + deltaY * outwardY;
        lateralDistance = Math.Abs(deltaX * outwardY - deltaY * outwardX);
        return longitudinalDistance >= 0.0 &&
               longitudinalDistance <= HighwayEndExtensionSnapLength &&
               lateralDistance <= Math.Max(
                   HighwayEndExtensionSnapHalfWidth,
                   HighwayRoadProfile.Get(port.Tier).RoadHalfWidthTiles +
                   1.0);
    }

    private static int GetPortDirectSnapRangeSquared(HighwayPort port)
    {
        var range = Math.Max(
            Math.Sqrt(PortSnapMaxDistanceSquared),
            HighwayRoadProfile.Get(port.Tier).RoadHalfWidthTiles + 1.0);
        return (int)Math.Ceiling(range * range);
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
            left.Value.OutboundNode == right.Value.OutboundNode &&
            left.Value.Tier == right.Value.Tier;
    }

    private bool AreAuthorizedAttachmentPortsOpen()
    {
        if (m_startAttachmentPort.HasValue &&
            !m_startAttachmentPortIsOpen)
        {
            return false;
        }

        if (!m_searchGoalPort.HasValue)
        {
            return true;
        }

        return IsGoalAttachmentPortOpen();
    }

    private bool IsGoalAttachmentPortOpen()
    {
        return m_searchGoalPort.HasValue &&
               m_searchGoalPortIsOpen;
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
        if (!IsGoalAttachmentPortOpen())
        {
            return false;
        }

        var lastPiece = m_pieces[m_pieces.Count - 1];
        GetLogicalRoadPorts(
            lastPiece,
            out _,
            out var physicalEndPort);
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
                    (
                        step.Proto.Id.Value,
                        step.Transform.IsReflected,
                        m_selectedTier),
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
        if (left.Proto.Tier != right.Proto.Tier ||
            left.Proto.LanesPerDirection != right.Proto.LanesPerDirection ||
            leftNodes.Length != rightNodes.Length ||
            leftNodes.Length < 4 ||
            (leftNodes.Length & 3) != 0)
        {
            return false;
        }

        // Every even lane follows the physical source-track orientation; its
        // following odd lane runs against it. Some train-plan pieces use a
        // prototype backwards, so
        // choose the directed lane endpoints in logical plan order first.
        var leftIsBackward = left.TrackDirection ==
                             TrainTrackTrajectoryDirection.Backward;
        var rightIsBackward = right.TrackDirection ==
                              TrainTrackTrajectoryDirection.Backward;
        for (var pairIndex = 0;
             pairIndex < left.Proto.LanesPerDirection;
             pairIndex++)
        {
            var forwardStartNode = pairIndex * 4;
            var forwardEndNode = forwardStartNode + 1;
            var reverseStartNode = forwardStartNode + 2;
            var reverseEndNode = forwardStartNode + 3;
            var leftForwardEnd = leftIsBackward
                ? leftNodes[reverseEndNode]
                : leftNodes[forwardEndNode];
            var leftReverseStart = leftIsBackward
                ? leftNodes[forwardStartNode]
                : leftNodes[reverseStartNode];
            var rightForwardStart = rightIsBackward
                ? rightNodes[reverseStartNode]
                : rightNodes[forwardStartNode];
            var rightReverseEnd = rightIsBackward
                ? rightNodes[forwardEndNode]
                : rightNodes[reverseEndNode];

            // Validate every directed lane exactly so a passing lane cannot
            // silently jump, terminate, or connect to the driving lane.
            if (leftForwardEnd != rightForwardStart ||
                rightReverseEnd != leftReverseStart)
            {
                return false;
            }
        }

        return true;
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
            !HasRoadSurfaceClearance(plan) ||
            !AllPreviewsValid() ||
            !TryGetExactPlanEndNode(plan, out var continuationNode) ||
            continuationNode.Direction.GradeFactor !=
                TrainTrackGradeFactor.G0 ||
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
        var supportedPieceCount = 0;
        for (var index = 0; index < pieceCount; index++)
        {
            var piece = m_pieces[index];
            configs[index] = new EntityConfigData(
                piece.Proto,
                m_configContext)
            {
                Transform = piece.Transform
            };
            if (ShouldCompleteImmediatelyOnSupports(piece))
            {
                supportedPieceCount++;
            }
        }

        // Adjacent pieces intentionally share their exact seam. The train
        // planner has already validated the route; generic batch validation
        // must not discard alternating curve/straight pieces because of that
        // planned overlap.
        // Keep the route in one command. If even one flat piece needs visible
        // supports, a construction truck cannot reach that piece before the
        // road itself exists, so the already-prevalidated connected plan must
        // complete together. Ground-only plans retain their normal costs.
        var completePlanImmediately =
            ShouldCompletePlanImmediately(supportedPieceCount);
        var command = m_inputScheduler.ScheduleInputCmd(
            new BatchCreateStaticEntitiesCmd(
                configs.GetImmutableArrayAndClear(),
                BuildMiniZippersMode.Never,
                isFree: completePlanImmediately,
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
            $"pieces, including {supportedPieceCount} supported G0 pieces; " +
            $"whole-plan immediate completion: " +
            $"{completePlanImmediately} " +
            $"(continue: {continueAfterBuild}).");
        return true;
    }

    private static bool ShouldCompletePlanImmediately(
        int supportedFlatPieceCount)
    {
        return supportedFlatPieceCount > 0;
    }

    private bool ShouldCompleteImmediatelyOnSupports(Piece piece)
    {
        if (piece.Proto.SourceTrackProto.HasElevationChange)
        {
            // G4/G8 prototypes already have EntityCosts.None because a truck
            // cannot deliver into the middle of an unfinished ramp.
            return false;
        }

        var maximumClearance = 0.0;
        foreach (var local in GetSupportClearanceLocalSamples(
                     piece.Proto.LanesTrajectories,
                     piece.Proto.VisualLaneWidthTiles / 2.0))
        {
            var world = piece.Proto.Layout
                .TransformPoint_RelToCenterTile(
                    local,
                    piece.Transform);
            var terrainTile = new Tile2i(
                world.X.ToIntFloored(),
                world.Y.ToIntFloored());
            if (!m_terrainManager.IsValidCoord(terrainTile))
            {
                continue;
            }

            maximumClearance = Math.Max(
                maximumClearance,
                world.Z.ToDouble() -
                m_terrainManager.GetHeight(world.Xy).Value.ToDouble());
        }

        return ShouldCompleteFlatPieceImmediately(maximumClearance);
    }

    private static IEnumerable<RelTile3f>
        GetSupportClearanceLocalSamples(
            ImmutableArray<RoadLaneTrajectory> lanes)
    {
        return GetSupportClearanceLocalSamples(
            lanes,
            laneHalfWidthTiles: 1.0);
    }

    private static IEnumerable<RelTile3f>
        GetSupportClearanceLocalSamples(
            ImmutableArray<RoadLaneTrajectory> lanes,
            double laneHalfWidthTiles)
    {
        // Sample each lane centre and both profile-specific outer edges. The
        // combined samples cover 4-, 8-, and 16-tile decks, including their
        // transverse slopes.
        foreach (var lane in lanes)
        {
            for (var index = 0;
                 index < lane.LaneCenterSamples.Length;
                 index++)
            {
                var center = lane.LaneCenterSamples[index];
                var lateral = lane.LaneDirectionSamples[index]
                    .Normalized.Xy.RightOrthogonalVector;
                var halfWidth = laneHalfWidthTiles.ToFix32();
                foreach (var lateralOffset in new[]
                         {
                             -halfWidth,
                             Fix32.Zero,
                             halfWidth
                         })
                {
                    yield return center + new RelTile3f(
                        lateralOffset * lateral,
                        Fix32.Zero);
                }
            }
        }
    }

    private static bool ShouldCompleteFlatPieceImmediately(
        double maximumTerrainClearance)
    {
        return maximumTerrainClearance >=
               MinimumVisibleSupportHeightTiles - 1e-8;
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
        m_searchGoalPortIsOpen = false;
        m_startAttachmentPort = null;
        m_startAttachmentPortIsOpen = false;
        m_allOpenPorts.Clear();
        m_openPorts.Clear();
        m_pendingPrimaryEndDirection = null;
        m_pendingPrimaryGoalPort = null;
        m_continuationStartNode = null;
        m_continuationPredecessor = null;
        m_builtPrefixStepCount = 0;
        m_directionIndex = 0;
        m_flatEndDirectionBaseIndex = 0;
        m_flatEndDirectionAttemptCount = 0;
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

/// <summary>
/// Gives each road family its own toolbar controller while sharing the costly
/// train planner, preview pools, and simulation callbacks of the real tool.
/// </summary>
public sealed class GroundRoadTierController : IUnityInputController
{
    private readonly GroundRoadDragController m_inner;

    public HighwayTier Tier { get; }

    public ControllerConfig Config => m_inner.Config;

    public bool IsActive =>
        m_inner.IsActive && m_inner.SelectedTier == Tier;

    public GroundRoadTierController(
        GroundRoadDragController inner,
        HighwayTier tier)
    {
        m_inner = inner;
        Tier = tier;
    }

    public void Activate()
    {
        m_inner.SelectTier(Tier);
        m_inner.SetActiveFacade(this);
        m_inner.Activate();
    }

    public void Deactivate()
    {
        if (m_inner.SelectedTier == Tier)
        {
            m_inner.Deactivate();
        }
    }

    public bool InputUpdate() => m_inner.InputUpdate();
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
        var vehicleCategory = protosDb.GetOrThrow<ToolbarCategoryProto>(
            GroundRoadIds.ToolbarCategory);
        var categories = ImmutableArray.Create(
            new ToolbarEntryData(vehicleCategory, order: 0));
        foreach (var profile in HighwayRoadProfile.All)
        {
            var representative = protosDb.All<HighwaySegmentProto>()
                .First(x => x.Tier == profile.Tier);
            var tierController = new GroundRoadTierController(
                controller,
                profile.Tier);
            var name = GroundRoadTexts.Localized(
                profile.ToolTextId + ".name");
            var description = GroundRoadTexts.Localized(
                profile.ToolTextId + ".description");

            hud.AddItem(
                new ControllerToolbarMenuItem(
                    context,
                    tierController,
                    name,
                    representative.IconPath,
                    categories,
                    extraLockingProto: null,
                    groupProto: null,
                    popupProto: representative,
                    floaterTitle: name,
                    floaterDesc: description,
                    order: (int)profile.Tier - 2));
        }

        AddNodeItem(
            hud,
            context,
            tIntersectionController,
            tIntersectionController.Prototype,
            categories,
            "tool.t-intersection",
            order: 10);
        AddNodeItem(
            hud,
            context,
            crossIntersectionController,
            crossIntersectionController.Prototype,
            categories,
            "tool.cross-intersection",
            order: 20);
        AddNodeItem(
            hud,
            context,
            roundaboutController,
            roundaboutController.Prototype,
            categories,
            "tool.roundabout",
            order: 30);

    }

    private static void AddNodeItem<TController>(
        ToolbarHud hud,
        UiContext context,
        TController controller,
        HighwayJunctionProto proto,
        ImmutableArray<ToolbarEntryData> categories,
        string textId,
        int order)
        where TController : HighwayNodePlacementControllerBase
    {
        var name = GroundRoadTexts.Localized(textId + ".name");
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
                floaterDesc:
                    GroundRoadTexts.Localized(textId + ".description"),
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
