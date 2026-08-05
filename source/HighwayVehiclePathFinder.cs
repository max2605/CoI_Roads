using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core;
using Mafi.Core.Entities.Dynamic;
using Mafi.Core.PathFinding;
using Mafi.Core.Roads;
using Mafi.Core.Vehicles;
using Mafi.PathFinding;

namespace GroundRoads;

/// <summary>
/// Adds a high-level highway decision around the game's original vehicle path
/// finder. The baseline keeps native terrain, roads, and bridges; optional
/// highway alternatives use terrain-only legs to exact highway graph nodes.
/// </summary>
public sealed class HighwayVehiclePathFinder : IVehiclePathFinder
{
    private enum Phase
    {
        None,
        Direct,
        ToHighway,
        FromHighway,
        Ready
    }

    private readonly IVehiclePathFinder m_inner;
    private readonly HighwayTrafficDirector m_trafficDirector;

    private Phase m_phase;
    private int m_currentPfId;
    private int m_totalSteps;
    private IVehiclePathFindingTask m_originalTask;
    private IVehiclePathFindingTask m_directTask;
    private FixedNoRoadTask m_activeLegTask;
    private List<HighwayTrafficDirector.Route> m_candidates;
    private int m_nextCandidateIndex;
    private HighwayTrafficDirector.Route? m_candidate;
    private IVehiclePathSegment m_deferredCompletedPath;
    private Tile2i m_deferredCompletedGoal;
    private bool m_deferredCandidateFailure;
    private string m_deferredCandidateFailureReason;
    private bool m_deferredStartLeg;
    private IVehiclePathSegment m_directPath;
    private IVehiclePathSegment m_entryPath;
    private IVehiclePathSegment m_resultPath;
    private Tile2i m_resultGoal;
    private Fix32 m_directCost;
    private Fix32 m_entryCost;

    public int CurrentPfId => m_currentPfId;

    public int TotalStepsCount => m_totalSteps + m_inner.TotalStepsCount;

    public Tile2i DistanceEstimationStartCoord =>
        m_originalTask?.DistanceEstimationStartTile ?? default;

    public Tile2i DistanceEstimationGoalCoord =>
        m_originalTask?.DistanceEstimationGoalTile ?? default;

    public IPathabilityProvider PathabilityProvider =>
        m_inner.PathabilityProvider;

    public HighwayVehiclePathFinder(
        ClearancePathabilityProvider clearancePathabilityProvider,
        RandomProvider randomProvider,
        IRoadsManager roadsManager,
        HighwayTrafficDirector trafficDirector)
    {
        m_trafficDirector = trafficDirector;
        var innerType = typeof(IVehiclePathFinder).Assembly.GetType(
            "Mafi.Core.PathFinding.VehiclePathFinder",
            throwOnError: true);
        var ctor = innerType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(ClearancePathabilityProvider),
                typeof(RandomProvider),
                typeof(IRoadsManager)
            },
            modifiers: null)
            ?? throw new MissingMethodException(
                innerType.FullName,
                ".ctor(ClearancePathabilityProvider, RandomProvider, " +
                "IRoadsManager)");

        m_inner = (IVehiclePathFinder)ctor.Invoke(
            new object[]
            {
                clearancePathabilityProvider,
                randomProvider,
                roadsManager
            });
    }

    public VehiclePathFinderInitResult InitVehiclePathFinding(
        IVehiclePathFindingTask task,
        ref int stepsLeft)
    {
        ResetState();
        m_currentPfId++;
        m_originalTask = task;
        // The baseline must retain the engine's complete routing behavior.
        // Disabling every road here also disabled ordinary roads and bridges.
        m_directTask = task;
        m_phase = Phase.Direct;

        var result = m_inner.InitVehiclePathFinding(
            m_directTask,
            ref stepsLeft);
        if (result == VehiclePathFinderInitResult.GoalAlreadyReached ||
            result == VehiclePathFinderInitResult.PathFound)
        {
            // A zero/trivial route cannot profit from a highway detour.
            if (m_inner.TryReconstructFoundPath(
                    out m_resultPath,
                    out m_resultGoal))
            {
                m_phase = Phase.Ready;
            }
        }

        return result;
    }

    public PathFinderResult ContinueVehiclePathFinding(
        ref int stepsLeft,
        bool newTaskStartedThisSimStep,
        IVehicleSurfaceProvider surfaceProvider,
        bool isFinalAttempt)
    {
        while (stepsLeft > 0)
        {
            if (m_deferredCompletedPath != null)
            {
                var completedPath = m_deferredCompletedPath;
                var completedGoal = m_deferredCompletedGoal;
                m_deferredCompletedPath = null;
                if (HandleCompletedPhase(
                        completedPath,
                        completedGoal,
                        ref stepsLeft,
                        out var deferredResult))
                {
                    return deferredResult;
                }

                continue;
            }

            if (m_deferredStartLeg)
            {
                m_deferredStartLeg = false;
                if (StartNextLegOrComplete(
                        "the terrain leg from the highway was invalid",
                        ref stepsLeft,
                        out var deferredResult))
                {
                    return deferredResult;
                }

                continue;
            }

            if (m_deferredCandidateFailure)
            {
                m_deferredCandidateFailure = false;
                var failureReason =
                    m_deferredCandidateFailureReason ??
                    "a highway access leg exhausted its path-finding budget";
                m_deferredCandidateFailureReason = null;
                if (TryStartNextCandidate(
                        failureReason,
                        ref stepsLeft,
                        out var deferredResult))
                {
                    return deferredResult;
                }

                continue;
            }

            var result = m_inner.ContinueVehiclePathFinding(
                ref stepsLeft,
                newTaskStartedThisSimStep,
                surfaceProvider,
                isFinalAttempt);
            newTaskStartedThisSimStep = false;

            if (result == PathFinderResult.StillSearching)
            {
                return result;
            }

            if (result == PathFinderResult.PathDoesNotExist)
            {
                if (m_phase == Phase.Direct)
                {
                    return result;
                }

                m_totalSteps += m_inner.TotalStepsCount;
                m_inner.ResetState();
                var failureReason = m_phase == Phase.FromHighway
                    ? "the terrain leg from the highway was unreachable"
                    : "the terrain leg to the highway was unreachable";
                if (TryStartNextCandidate(
                        failureReason,
                        ref stepsLeft,
                        out var candidateResult))
                {
                    return candidateResult;
                }

                continue;
            }

            if (!m_inner.TryReconstructFoundPath(
                    out var foundPath,
                    out var foundGoal))
            {
                if (m_phase == Phase.Direct)
                {
                    return PathFinderResult.PathDoesNotExist;
                }

                m_totalSteps += m_inner.TotalStepsCount;
                m_inner.ResetState();
                var failureReason = m_phase == Phase.FromHighway
                    ? "the terrain leg from the highway could not be " +
                        "reconstructed"
                    : "the terrain leg to the highway could not be " +
                        "reconstructed";
                if (TryStartNextCandidate(
                        failureReason,
                        ref stepsLeft,
                        out var candidateResult))
                {
                    return candidateResult;
                }

                continue;
            }

            m_totalSteps += m_inner.TotalStepsCount;
            m_inner.ResetState();

            if (HandleCompletedPhase(
                    foundPath,
                    foundGoal,
                    ref stepsLeft,
                    out var completedResult))
            {
                return completedResult;
            }
        }

        return PathFinderResult.StillSearching;
    }

    public bool? TryExtendGoals()
    {
        if (m_deferredCompletedPath != null || m_deferredCandidateFailure ||
            m_deferredStartLeg)
        {
            return false;
        }

        var result = m_inner.TryExtendGoals();
        if (result == false)
        {
            return false;
        }

        if (!result.HasValue)
        {
            if (m_phase == Phase.Direct)
            {
                return null;
            }

            // A highway leg shares the original task's outer budget. If it
            // runs out, preserve the already-found direct route and continue
            // with the next candidate on the manager's next budget slice.
            m_totalSteps += m_inner.TotalStepsCount;
            m_inner.ResetState();
            m_deferredCandidateFailure = true;
            m_deferredCandidateFailureReason =
                m_phase == Phase.FromHighway
                    ? "the terrain leg from the highway exhausted its " +
                        "path-finding budget"
                    : "the terrain leg to the highway exhausted its " +
                        "path-finding budget";
            return false;
        }

        if (m_inner.TryReconstructFoundPath(
                out var foundPath,
                out var foundGoal))
        {
            m_totalSteps += m_inner.TotalStepsCount;
            m_inner.ResetState();
            m_deferredCompletedPath = foundPath;
            m_deferredCompletedGoal = foundGoal;
            // Returning true would make the manager finalize the outer task
            // before the wrapper can advance its multi-phase state machine.
            return false;
        }

        if (m_phase == Phase.Direct)
        {
            // Match the native GoalAlreadyReached behavior: a successful task
            // may legitimately have no path segment to reconstruct.
            m_resultGoal = foundGoal;
            m_phase = Phase.Ready;
            return true;
        }

        m_totalSteps += m_inner.TotalStepsCount;
        m_inner.ResetState();
        m_deferredCandidateFailure = true;
        m_deferredCandidateFailureReason =
            m_phase == Phase.FromHighway
                ? "the terrain leg from the highway could not be " +
                    "reconstructed"
                : "the terrain leg to the highway could not be " +
                    "reconstructed";
        return false;
    }

    public bool TryReconstructFoundPath(
        out IVehiclePathSegment firstPathSegment,
        out Tile2i goalTileRaw)
    {
        firstPathSegment = m_resultPath;
        goalTileRaw = m_resultGoal;
        return m_phase == Phase.Ready && firstPathSegment != null;
    }

    public void GetExploredTiles(Lyst<ExploredPfNode> exploredTiles)
    {
        m_inner.GetExploredTiles(exploredTiles);
    }

    public void ResetState()
    {
        m_inner?.ResetState();
        m_phase = Phase.None;
        m_totalSteps = 0;
        m_originalTask = null;
        m_directTask = null;
        m_activeLegTask = null;
        m_candidates = null;
        m_nextCandidateIndex = 0;
        m_candidate = null;
        m_deferredCompletedPath = null;
        m_deferredCompletedGoal = default;
        m_deferredCandidateFailure = false;
        m_deferredCandidateFailureReason = null;
        m_deferredStartLeg = false;
        m_directPath = null;
        m_entryPath = null;
        m_resultPath = null;
        m_resultGoal = default;
        m_directCost = Fix32.Zero;
        m_entryCost = Fix32.Zero;
    }

    public Tile2i? FindClosestValidPosition<T>(
        Tile2i coord,
        VehiclePathFindingParams pfParams,
        Predicate<VehiclePfNode> predicateGround = null,
        Predicate<PfNodeInfo> predicateWater = null)
    {
        return m_inner.FindClosestValidPosition<T>(
            coord,
            pfParams,
            predicateGround,
            predicateWater);
    }

    private bool HandleCompletedPhase(
        IVehiclePathSegment foundPath,
        Tile2i foundGoal,
        ref int stepsLeft,
        out PathFinderResult completedResult)
    {
        completedResult = PathFinderResult.StillSearching;
        switch (m_phase)
        {
            case Phase.Direct:
                m_directPath = foundPath;
                m_directCost = ComputePathLength(foundPath);
                m_resultGoal = foundGoal;

                if (!m_trafficDirector.TryCreateRoutes(
                        m_originalTask.Vehicle,
                        m_directTask.StartTiles,
                        m_directTask.GoalTiles,
                        m_originalTask.PathFindingParams,
                        m_directCost,
                        out var candidates))
                {
                    SelectDirectResult("no suitable highway route found");
                    completedResult = PathFinderResult.PathFound;
                    return true;
                }

                m_candidates = candidates;
                m_nextCandidateIndex = 0;
                return TryStartNextCandidate(
                    "no reachable highway candidate found",
                    ref stepsLeft,
                    out completedResult);

            case Phase.ToHighway:
                m_entryPath = foundPath;
                m_entryCost = ComputePathLength(foundPath) +
                    m_candidate.Value.EntryConnectorDistance;
                m_activeLegTask = FixedNoRoadTask.Create(
                    m_originalTask,
                    SingleTile(m_candidate.Value.ExitTile),
                    CopyTiles(m_directTask.GoalTiles),
                    m_candidate.Value.ExitTile,
                    m_originalTask.DistanceEstimationGoalTile,
                    keepOriginalGoalDirection: true,
                    allStartDirectionsAllowed: true,
                    navigateClosebyIsSufficient:
                        m_originalTask.NavigateClosebyIsSufficient);
                m_phase = Phase.FromHighway;
                return StartNextLegOrComplete(
                    "the terrain leg from the highway was invalid",
                    ref stepsLeft,
                    out completedResult);

            case Phase.FromHighway:
                var exitCost = ComputePathLength(foundPath) +
                    m_candidate.Value.ExitConnectorDistance;
                if (!m_trafficDirector.ShouldUseHighway(
                        m_directCost,
                        m_entryCost,
                        m_candidate.Value.RoadDistance,
                        exitCost,
                        out var highwayCost))
                {
                    return TryStartNextCandidate(
                        $"highway cost {highwayCost} exceeds the " +
                        $"preferred range around direct cost {m_directCost}",
                        ref stepsLeft,
                        out completedResult);
                }

                if (!m_trafficDirector.IsRouteStillUsable(
                        m_candidate.Value,
                        m_originalTask.PathFindingParams))
                {
                    return TryStartNextCandidate(
                        "the selected highway changed while routing",
                        ref stepsLeft,
                        out completedResult);
                }

                m_resultPath = JoinHighwayPath(
                    m_entryPath,
                    m_candidate.Value.RoadPath,
                    foundPath);
                m_resultGoal = foundGoal;
                m_phase = Phase.Ready;
                Log.Info(
                    $"GroundRoads: vehicle #{m_originalTask.Vehicle.Id} " +
                    $"uses highway ({highwayCost} vs direct " +
                    $"{m_directCost}, equal-cost preference enabled).");
                completedResult = PathFinderResult.PathFound;
                return true;

            case Phase.Ready:
                completedResult = PathFinderResult.PathFound;
                return true;

            default:
                completedResult = PathFinderResult.PathDoesNotExist;
                return true;
        }
    }

    private bool StartNextLegOrComplete(
        string invalidReason,
        ref int stepsLeft,
        out PathFinderResult completedResult)
    {
        if (stepsLeft <= 0)
        {
            m_deferredStartLeg = true;
            completedResult = PathFinderResult.StillSearching;
            return false;
        }

        var init = m_inner.InitVehiclePathFinding(
            m_activeLegTask,
            ref stepsLeft);
        if (init == VehiclePathFinderInitResult.ReadyForPf)
        {
            completedResult = PathFinderResult.StillSearching;
            return false;
        }

        if (init == VehiclePathFinderInitResult.PathFound ||
            init == VehiclePathFinderInitResult.GoalAlreadyReached)
        {
            if (m_inner.TryReconstructFoundPath(
                    out var immediatePath,
                    out var immediateGoal))
            {
                m_totalSteps += m_inner.TotalStepsCount;
                m_inner.ResetState();
                return HandleCompletedPhase(
                    immediatePath,
                    immediateGoal,
                    ref stepsLeft,
                    out completedResult);
            }
        }

        m_totalSteps += m_inner.TotalStepsCount;
        m_inner.ResetState();
        return TryStartNextCandidate(
            $"{invalidReason}; init returned {init}",
            ref stepsLeft,
            out completedResult);
    }

    private bool TryStartNextCandidate(
        string previousFailureReason,
        ref int stepsLeft,
        out PathFinderResult completedResult)
    {
        while (m_candidates != null &&
               m_nextCandidateIndex < m_candidates.Count)
        {
            if (stepsLeft <= 0)
            {
                m_deferredCandidateFailure = true;
                m_deferredCandidateFailureReason =
                    previousFailureReason;
                completedResult = PathFinderResult.StillSearching;
                return false;
            }

            var candidate = m_candidates[m_nextCandidateIndex++];
            if (!m_trafficDirector.IsRouteStillUsable(
                    candidate,
                    m_originalTask.PathFindingParams))
            {
                previousFailureReason =
                    "the selected highway changed before its access leg";
                continue;
            }

            m_candidate = candidate;
            m_entryPath = null;
            m_entryCost = Fix32.Zero;
            m_activeLegTask = FixedNoRoadTask.Create(
                m_originalTask,
                CopyTiles(m_directTask.StartTiles),
                SingleTile(candidate.EntryTile),
                m_originalTask.DistanceEstimationStartTile,
                candidate.EntryTile,
                keepOriginalGoalDirection: false,
                allStartDirectionsAllowed:
                    m_originalTask.AllStartDirectionsAllowed,
                navigateClosebyIsSufficient: false);
            m_phase = Phase.ToHighway;

            var init = m_inner.InitVehiclePathFinding(
                m_activeLegTask,
                ref stepsLeft);
            if (init == VehiclePathFinderInitResult.ReadyForPf)
            {
                completedResult = PathFinderResult.StillSearching;
                return false;
            }

            if ((init == VehiclePathFinderInitResult.PathFound ||
                 init == VehiclePathFinderInitResult.GoalAlreadyReached) &&
                m_inner.TryReconstructFoundPath(
                    out var immediatePath,
                    out var immediateGoal))
            {
                m_totalSteps += m_inner.TotalStepsCount;
                m_inner.ResetState();
                return HandleCompletedPhase(
                    immediatePath,
                    immediateGoal,
                    ref stepsLeft,
                    out completedResult);
            }

            m_totalSteps += m_inner.TotalStepsCount;
            m_inner.ResetState();
            previousFailureReason =
                $"entry leg initialization returned {init}";
        }

        SelectDirectResult(previousFailureReason);
        completedResult = PathFinderResult.PathFound;
        return true;
    }

    private void SelectDirectResult(string reason)
    {
        m_resultPath = m_directPath;
        m_phase = Phase.Ready;

        // Falling back without any cost-effective candidates is normal for
        // short trips and stays silent. If candidates existed but every
        // native access leg failed, keep one compact diagnostic per route so
        // a destination-specific regression can be identified from the log.
        if (m_candidates != null && m_candidates.Count > 0)
        {
            Log.Info(
                $"GroundRoads: vehicle #{m_originalTask.Vehicle.Id} falls " +
                $"back to its native route after testing " +
                $"{m_nextCandidateIndex}/{m_candidates.Count} highway " +
                $"candidates ({reason}).");
        }
    }

    private static IVehiclePathSegment JoinHighwayPath(
        IVehiclePathSegment entryPath,
        ImmutableArray<RoadPathSegment> roadPath,
        IVehiclePathSegment exitPath)
    {
        // PathFindingEntity validates only the first road piece's target
        // before handing the complete path to DrivingEntity. The traffic
        // director validates that target from the resolved terrain access
        // tile, so keep the entire highway leg in one native road segment.
        // Splitting it would make the engine leave and re-enter road mode at
        // every artificial boundary, causing vehicles to brake and weave.
        var roadSegment = new VehicleRoadPathSegment();
        for (var index = 0; index < roadPath.Length; index++)
        {
            roadSegment.PathReversed.Add(roadPath[index]);
        }

        roadSegment.PathReversed.Reverse();
        roadSegment.NextSegment = Option.Some(exitPath);
        SetNextSegment(entryPath.FindLastSegment(), roadSegment);
        return entryPath;
    }

    private static void SetNextSegment(
        IVehiclePathSegment segment,
        IVehiclePathSegment next)
    {
        switch (segment)
        {
            case VehicleTerrainPathSegment terrain:
                terrain.NextSegment = Option.Some(next);
                break;
            case VehicleRoadPathSegment road:
                road.NextSegment = Option.Some(next);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported vehicle path segment '{segment.GetType()}'.");
        }
    }

    private static Fix32 ComputePathLength(IVehiclePathSegment first)
    {
        var total = Fix32.Zero;
        var current = Option.Some(first);
        while (current.HasValue)
        {
            switch (current.Value)
            {
                case VehicleTerrainPathSegment terrain:
                    for (var i = 1; i < terrain.PathRawReversed.Count; i++)
                    {
                        total += terrain.PathRawReversed[i - 1]
                            .DistanceTo(terrain.PathRawReversed[i]);
                    }
                    break;
                case VehicleRoadPathSegment road:
                    foreach (var roadPiece in road.PathReversed)
                    {
                        total += roadPiece.Entity.RoadProto
                            .LanesData[roadPiece.LaneIndex].LaneLength.Value;
                    }
                    break;
            }

            current = current.Value.NextSegment;
        }

        return total;
    }

    private static Lyst<Tile2i> CopyTiles(
        IIndexable<Tile2i> tiles)
    {
        var result = new Lyst<Tile2i>(tiles.Count);
        foreach (var tile in tiles)
        {
            result.Add(tile);
        }

        return result;
    }

    private static Lyst<Tile2i> SingleTile(Tile2i tile)
    {
        var result = new Lyst<Tile2i>(1);
        result.Add(tile);
        return result;
    }

    private static VehiclePathFindingParams CreateNoRoadParams(
        VehiclePathFindingParams source)
    {
        // MaxWheelSubmerge uses MaxValue as the stored sentinel for ordinary
        // non-amphibious vehicles. Passing that stored value back into the
        // constructor would incorrectly switch OceanPathability to
        // AllowOcean and create an unregistered pathability class.
        var maxWheelSubmerge =
            source.OceanPathability == OceanPathability.AllowOcean
                ? source.MaxWheelSubmerge
                : ThicknessTilesF.Zero;
        return new VehiclePathFindingParams(
            source.MinSizeClearance,
            source.SteepnessPathability,
            source.HeightClearancePathability,
            source.MaterialTraversalSensitivity,
            RoadLaneType.MaskAllowNone,
            maxWheelSubmerge);
    }

    private sealed class FixedNoRoadTask : IVehiclePathFindingTask
    {
        private readonly IVehiclePathFindingTask m_source;

        private FixedNoRoadTask(
            IVehiclePathFindingTask source,
            Lyst<Tile2i> starts,
            Lyst<Tile2i> goals,
            Tile2i distanceEstimationStartTile,
            Tile2i distanceEstimationGoalTile,
            bool keepOriginalGoalDirection,
            bool allStartDirectionsAllowed,
            bool navigateClosebyIsSufficient)
        {
            m_source = source;
            StartTiles = starts;
            GoalTiles = goals;
            DistanceEstimationStartTile =
                distanceEstimationStartTile;
            DistanceEstimationGoalTile =
                distanceEstimationGoalTile;
            PathFindingParams = CreateNoRoadParams(
                source.PathFindingParams);
            GoalDirection = keepOriginalGoalDirection
                ? source.GoalDirection
                : null;
            AllStartDirectionsAllowed = allStartDirectionsAllowed;
            NavigateClosebyIsSufficient =
                navigateClosebyIsSufficient;
        }

        public static FixedNoRoadTask Create(
            IVehiclePathFindingTask source,
            Lyst<Tile2i> starts,
            Lyst<Tile2i> goals,
            Tile2i distanceEstimationStartTile,
            Tile2i distanceEstimationGoalTile,
            bool keepOriginalGoalDirection,
            bool allStartDirectionsAllowed,
            bool navigateClosebyIsSufficient)
        {
            return new FixedNoRoadTask(
                source,
                starts,
                goals,
                distanceEstimationStartTile,
                distanceEstimationGoalTile,
                keepOriginalGoalDirection,
                allStartDirectionsAllowed,
                navigateClosebyIsSufficient);
        }

        public IPathFindingVehicle Vehicle => m_source.Vehicle;
        public VehiclePathFindingParams PathFindingParams { get; }
        public int MaxRetries => 0;
        public RelTile1i ExtraTolerancePerRetry => RelTile1i.Zero;
        public bool AllowSimplePathOnly => m_source.AllowSimplePathOnly;
        public bool NavigateClosebyIsSufficient { get; }
        public bool SkipConnectivityCheck => true;
        public RelTile1f MaxNavigateClosebyDistance =>
            m_source.MaxNavigateClosebyDistance;
        public ThicknessTilesF MaxNavigateClosebyHeightDifference =>
            m_source.MaxNavigateClosebyHeightDifference;
        public bool HasResult => false;
        public IIndexable<Tile2i> StartTiles { get; }
        public Tile2i DistanceEstimationStartTile { get; }
        public IIndexable<Tile2i> GoalTiles { get; }
        public AngleDegrees1f? GoalDirection { get; }
        public Tile2i DistanceEstimationGoalTile { get; }
        public bool IsToEdgeOfMap => false;
        public bool AllStartDirectionsAllowed { get; }

        public bool InitializeStartAndGoals(int retryNumber)
        {
            return false;
        }
    }
}
