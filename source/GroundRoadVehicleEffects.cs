using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Dynamic;
using Mafi.Core.Prototypes;
using Mafi.Core.Roads;
using Mafi.Core.SaveGame;
using Mafi.Core.Simulation;
using Mafi.Core.Terrain;
using Mafi.Core.Vehicles;

namespace GroundRoads;

/// <summary>
/// Applies the Ground Roads driving bonus without persisting any runtime
/// callbacks or transient vehicle state into the save game.
/// </summary>
public sealed class GroundRoadVehicleEffects : IDisposable
{
    private delegate void SetGroundPositionDelegate(
        DynamicGroundEntity entity,
        Tile2f position);

    private delegate void SetPositionDelegate(
        DynamicGroundEntity entity,
        Tile3f position);

    private delegate void SetDirectionDelegate(
        DynamicGroundEntity entity,
        AngleDegrees1f direction);

    private static readonly Percent RoadSpeedFactor = 140.Percent();
    private static readonly Percent GroundRoadMaintenanceFactor = 50.Percent();

    private static readonly FieldInfo DrivingMaxForwardSpeedField =
        typeof(DrivingData).GetField(
            nameof(DrivingData.MaxForwardsSpeed),
            BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingFieldException(
            nameof(DrivingData),
            nameof(DrivingData.MaxForwardsSpeed));

    private static readonly FieldInfo SpeedDriverField =
        typeof(DrivingEntity).GetField(
            "m_speedDriver",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            nameof(DrivingEntity),
            "m_speedDriver");

    private static readonly FieldInfo SteeringDriverField =
        typeof(DrivingEntity).GetField(
            "m_steeringDriver",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            nameof(DrivingEntity),
            "m_steeringDriver");

    private static readonly FieldInfo CurrentRoadDirectionField =
        typeof(DrivingEntity).GetField(
            "m_currentRoadDirection",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            nameof(DrivingEntity),
            "m_currentRoadDirection");

    private static readonly FieldInfo DriverMaxForwardSpeedBaseField =
        typeof(SmoothDriver).GetField(
            "m_maxForwardsSpeedBase",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(
            nameof(SmoothDriver),
            "m_maxForwardsSpeedBase");

    private static readonly SetGroundPositionDelegate SetGroundPosition =
        (SetGroundPositionDelegate)typeof(DynamicGroundEntity)
            .GetMethod(
                "SetGroundPosition",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(Tile2f) },
                modifiers: null)
            ?.CreateDelegate(typeof(SetGroundPositionDelegate))
        ?? throw new MissingMethodException(
            nameof(DynamicGroundEntity),
            "SetGroundPosition(Tile2f)");

    private static readonly SetPositionDelegate SetPosition =
        (SetPositionDelegate)typeof(DynamicGroundEntity)
            .GetMethod(
                "SetPosition",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(Tile3f) },
                modifiers: null)
            ?.CreateDelegate(typeof(SetPositionDelegate))
        ?? throw new MissingMethodException(
            nameof(DynamicGroundEntity),
            "SetPosition(Tile3f)");

    private static readonly SetDirectionDelegate SetDirection =
        (SetDirectionDelegate)typeof(DynamicGroundEntity)
            .GetProperty(
                nameof(DynamicGroundEntity.Direction),
                BindingFlags.Instance | BindingFlags.Public)
            ?.GetSetMethod(nonPublic: true)
            ?.CreateDelegate(typeof(SetDirectionDelegate))
        ?? throw new MissingMethodException(
            nameof(DynamicGroundEntity),
            "set_Direction(AngleDegrees1f)");

    private readonly IVehiclesManager m_vehiclesManager;
    private readonly IEntitiesManager m_entitiesManager;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly SaveManager m_saveManager;
    private readonly TerrainManager m_terrainManager;
    private readonly Dictionary<string, DrivingData> m_drivingDataByProto = new();
    private readonly Dictionary<string, RelTile1f> m_originalSpeeds = new();
    private readonly Dictionary<string, RelTile1f> m_boostedSpeeds = new();
    private readonly HashSet<Vehicle> m_boostedVehicles = new();
    private readonly HashSet<Vehicle> m_vehiclesPresentAtUpdateStart = new();

    private bool m_arePrototypesBoosted;
    private bool m_restoreMaintenanceAfterSave;
    private bool m_isDisposed;

    public GroundRoadVehicleEffects(
        IVehiclesManager vehiclesManager,
        IEntitiesManager entitiesManager,
        ISimLoopEvents simLoopEvents,
        SaveManager saveManager,
        TerrainManager terrainManager,
        ProtosDb protosDb)
    {
        m_vehiclesManager = vehiclesManager;
        m_entitiesManager = entitiesManager;
        m_simLoopEvents = simLoopEvents;
        m_saveManager = saveManager;
        m_terrainManager = terrainManager;

        // Keep the prototype ceiling at 140% so road driving is allowed to
        // request that speed. The SmoothDriver ceiling is adjusted per vehicle
        // below, which keeps terrain driving at the original speed.
        foreach (var vehicleProto in protosDb.All<DrivingEntityProto>())
        {
            // Ships and other non-road driving entities must keep their native
            // speed. Every prototype that can use a road lane is eligible.
            if (vehicleProto.PathFindingParams.RoadLaneTypeMask ==
                RoadLaneType.MaskAllowNone)
            {
                continue;
            }

            var key = vehicleProto.Id.Value;
            var original = vehicleProto.DrivingData.MaxForwardsSpeed;
            var boosted = original.ScaledBy(RoadSpeedFactor);

            m_originalSpeeds[key] = original;
            m_boostedSpeeds[key] = boosted;
            m_drivingDataByProto[key] = vehicleProto.DrivingData;
        }

        // Repair driver bases that may have been serialized by an older mod
        // version while its transient boost was active.
        NormalizeAllVehicleDrivers();

        m_entitiesManager.EntityAdded.AddNonSaveable(
            this,
            OnEntityAdded);
        m_saveManager.OnSaveDone += OnSaveDone;
        m_simLoopEvents.UpdateStart.AddNonSaveable(
            this,
            OnUpdateStart);
        m_simLoopEvents.Update.AddNonSaveable(
            this,
            OnSimulationUpdate);
        m_simLoopEvents.UpdateEnd.AddNonSaveable(
            this,
            OnUpdateEnd);
        m_simLoopEvents.BeforeSave.AddNonSaveable(
            this,
            OnBeforeSave);

        Log.Info(
            "GroundRoads: vehicle effects active on native Ground Road " +
            "entities (direct driver speed 140%, maintenance 50%, " +
            "lane projection enabled on highway segments; junctions use " +
            "native steering).");
    }

    public void Dispose()
    {
        if (m_isDisposed)
        {
            return;
        }

        m_isDisposed = true;
        m_entitiesManager.EntityAdded.RemoveNonSaveable(
            this,
            OnEntityAdded);
        m_saveManager.OnSaveDone -= OnSaveDone;
        m_simLoopEvents.UpdateStart.RemoveNonSaveable(
            this,
            OnUpdateStart);
        m_simLoopEvents.Update.RemoveNonSaveable(
            this,
            OnSimulationUpdate);
        m_simLoopEvents.UpdateEnd.RemoveNonSaveable(
            this,
            OnUpdateEnd);
        m_simLoopEvents.BeforeSave.RemoveNonSaveable(
            this,
            OnBeforeSave);

        RestoreTransientSpeedBoosts(
            clampVehiclesOutsideGroundRoad: true);
        m_restoreMaintenanceAfterSave = false;

        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            if (vehicle.IsDestroyed)
            {
                continue;
            }

            if (m_originalSpeeds.TryGetValue(
                    vehicle.Prototype.Id.Value,
                out var original))
            {
                SetDriverMaxForwardSpeed(
                    vehicle,
                    original,
                    vehicle.SpeedFactor);
                ClampDriverToCurrentLimit(vehicle);
            }

            // Remove the mod's 50% modifier immediately on hot reload. The
            // native road/surface/terrain factor is recomputed by the same
            // path used during regular updates.
            ApplyMaintenanceFactor(vehicle, isOnGroundRoad: false);
        }

        foreach (var originalSpeed in m_originalSpeeds)
        {
            // Prototypes are recreated on every game launch. Restoring them is
            // mainly useful for non-locking DLL reloads during development.
            if (m_drivingDataByProto.TryGetValue(
                    originalSpeed.Key,
                    out var drivingData))
            {
                DrivingMaxForwardSpeedField.SetValue(
                    drivingData,
                    originalSpeed.Value);
            }
        }

        m_drivingDataByProto.Clear();
        m_originalSpeeds.Clear();
        m_boostedSpeeds.Clear();
        m_boostedVehicles.Clear();
        m_vehiclesPresentAtUpdateStart.Clear();
    }

    private void OnUpdateStart()
    {
        RestoreMaintenanceAfterSaveIfNeeded();

        // A previous interrupted update or hot reload must never leak a
        // transient boost into the next simulation step.
        RestoreTransientSpeedBoosts(
            clampVehiclesOutsideGroundRoad: true);

        foreach (var boostedSpeed in m_boostedSpeeds)
        {
            DrivingMaxForwardSpeedField.SetValue(
                m_drivingDataByProto[boostedSpeed.Key],
                boostedSpeed.Value);
        }

        m_arePrototypesBoosted = true;
        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            if (vehicle.IsDestroyed || !m_boostedSpeeds.TryGetValue(
                    vehicle.Prototype.Id.Value,
                    out var boosted))
            {
                continue;
            }

            m_vehiclesPresentAtUpdateStart.Add(vehicle);
            if (!IsOnGroundRoad(vehicle))
            {
                continue;
            }

            SetDriverMaxForwardSpeed(
                vehicle,
                boosted,
                vehicle.SpeedFactor);
            m_boostedVehicles.Add(vehicle);
        }
    }

    private void OnSimulationUpdate()
    {
        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            if (vehicle.IsDestroyed)
            {
                continue;
            }

            TrackVehicleCreatedDuringBoostWindow(vehicle);
            var isOnGroundRoad = IsOnGroundRoad(vehicle);
            if (ShouldProjectToHighwayLane(vehicle))
            {
                SnapToCurrentHighwayLane(vehicle);
            }

            ApplyMaintenanceFactor(vehicle, isOnGroundRoad);
        }
    }

    private void OnUpdateEnd()
    {
        RestoreTransientSpeedBoosts(
            clampVehiclesOutsideGroundRoad: true);
    }

    private void OnBeforeSave()
    {
        RestoreTransientSpeedBoosts(
            clampVehiclesOutsideGroundRoad: true);
        m_restoreMaintenanceAfterSave = true;
        NormalizeAllVehicleMaintenance();
    }

    private void OnSaveDone(SaveResult result)
    {
        _ = result;
        try
        {
            RestoreMaintenanceAfterSaveIfNeeded();
        }
        catch (Exception exception)
        {
            // SaveManager invokes this .NET event directly. Never let a
            // best-effort post-save restoration disrupt save completion; the
            // flag remains set so UpdateStart can safely retry on the sim
            // thread.
            Log.Exception(
                exception,
                "GroundRoads failed to restore maintenance after saving; " +
                "the next simulation update will retry.");
        }
    }

    private void OnEntityAdded(IEntity entity)
    {
        if (entity is not Vehicle vehicle || vehicle.IsDestroyed ||
            !m_originalSpeeds.TryGetValue(
                vehicle.Prototype.Id.Value,
                out var original))
        {
            return;
        }

        // A DrivingEntity copies the currently active prototype maximum into
        // its saveable SmoothDriver in its constructor. If construction occurs
        // while prototypes are transiently boosted, normalize the new driver
        // before it can participate in another update or save. This also
        // repairs a vehicle whose construction and world insertion straddle
        // the end of the boosted update window.
        if (m_arePrototypesBoosted ||
            IsDriverUsingBoostedBase(vehicle))
        {
            SetDriverMaxForwardSpeed(
                vehicle,
                original,
                vehicle.SpeedFactor);
        }

        if (m_arePrototypesBoosted)
        {
            m_vehiclesPresentAtUpdateStart.Add(vehicle);
        }
    }

    private void RestoreTransientSpeedBoosts(
        bool clampVehiclesOutsideGroundRoad = false)
    {
        CaptureVehiclesCreatedDuringBoostWindow();

        foreach (var vehicle in m_boostedVehicles)
        {
            if (!vehicle.IsDestroyed && m_originalSpeeds.TryGetValue(
                    vehicle.Prototype.Id.Value,
                    out var original))
            {
                SetDriverMaxForwardSpeed(
                    vehicle,
                    original,
                    vehicle.SpeedFactor);
                if (clampVehiclesOutsideGroundRoad &&
                    !IsOnGroundRoad(vehicle))
                {
                    ClampDriverToCurrentLimit(vehicle);
                }
            }
        }

        m_boostedVehicles.Clear();
        m_vehiclesPresentAtUpdateStart.Clear();
        if (!m_arePrototypesBoosted)
        {
            return;
        }

        foreach (var originalSpeed in m_originalSpeeds)
        {
            DrivingMaxForwardSpeedField.SetValue(
                m_drivingDataByProto[originalSpeed.Key],
                originalSpeed.Value);
        }

        m_arePrototypesBoosted = false;
    }

    private void CaptureVehiclesCreatedDuringBoostWindow()
    {
        if (!m_arePrototypesBoosted)
        {
            return;
        }

        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            TrackVehicleCreatedDuringBoostWindow(vehicle);
        }
    }

    private void TrackVehicleCreatedDuringBoostWindow(Vehicle vehicle)
    {
        if (!m_arePrototypesBoosted || vehicle.IsDestroyed ||
            !m_originalSpeeds.ContainsKey(vehicle.Prototype.Id.Value) ||
            !m_vehiclesPresentAtUpdateStart.Add(vehicle))
        {
            return;
        }

        // If EntityAdded was unavailable for a particular creation path, the
        // driver may have copied the boosted prototype value. Include it in
        // the regular end-of-step restoration set as a defensive fallback.
        m_boostedVehicles.Add(vehicle);
    }

    private void NormalizeAllVehicleDrivers()
    {
        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            if (!vehicle.IsDestroyed && m_originalSpeeds.TryGetValue(
                    vehicle.Prototype.Id.Value,
                    out var original))
            {
                SetDriverMaxForwardSpeed(
                    vehicle,
                    original,
                    vehicle.SpeedFactor);
                if (!IsOnGroundRoad(vehicle))
                {
                    ClampDriverToCurrentLimit(vehicle);
                }
            }
        }
    }

    private void NormalizeAllVehicleMaintenance()
    {
        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            if (!vehicle.IsDestroyed)
            {
                ApplyMaintenanceFactor(vehicle, isOnGroundRoad: false);
            }
        }
    }

    private void RestoreMaintenanceAfterSaveIfNeeded()
    {
        if (!m_restoreMaintenanceAfterSave || m_isDisposed)
        {
            return;
        }

        foreach (var vehicle in m_vehiclesManager.AllVehicles)
        {
            if (!vehicle.IsDestroyed)
            {
                ApplyMaintenanceFactor(vehicle, IsOnGroundRoad(vehicle));
            }
        }

        m_restoreMaintenanceAfterSave = false;
    }

    private void ApplyMaintenanceFactor(
        Vehicle vehicle,
        bool isOnGroundRoad)
    {
        Percent factor;
        if (isOnGroundRoad)
        {
            factor = GroundRoadMaintenanceFactor;
        }
        else if (vehicle.IsDrivingOnRoad)
        {
            factor = RoadEntityProtoBase.ROAD_MAINTENANCE_SCALE;
        }
        else if (m_terrainManager.TryGetTileSurface(
                     m_terrainManager.GetTileIndex(
                         vehicle.GroundPositionTile2i),
                     out var surfaceData))
        {
            factor = surfaceData.SurfaceSlimId
                .AsProtoOrPhantom(m_terrainManager)
                .MaintenanceScale;
        }
        else
        {
            factor = Percent.Hundred;
        }

        vehicle.Maintenance.SetDynamicExtraMultiplier(factor);
    }

    private bool IsOnGroundRoad(Vehicle vehicle)
    {
        if (vehicle.IsDrivingOnRoad && vehicle.CurrentRoadEntity.HasValue)
        {
            var roadProto = vehicle.CurrentRoadEntity.Value.RoadProto;
            if (roadProto is IHighwayNetworkProto ||
                roadProto is GroundRoadProto ||
                roadProto is GroundRoadEntranceProto ||
                roadProto is GroundRoadAccessibleSegmentProto)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldProjectToHighwayLane(Vehicle vehicle)
    {
        return vehicle.IsDrivingOnRoad &&
            vehicle.CurrentRoadEntity.HasValue &&
            RequiresLaneProjection(
                vehicle.CurrentRoadEntity.Value.RoadProto);
    }

    private static bool RequiresLaneProjection(
        IRoadGraphEntityProto roadProto)
    {
        // The custom correction is needed only on the long train-planned
        // highway curves. Junctions have short, tightly connected turns and
        // must retain the game's native steering across entity boundaries.
        return roadProto is HighwaySegmentProto;
    }

    private static void SnapToCurrentHighwayLane(Vehicle vehicle)
    {
        var projected = vehicle.FindClosestPointOnRoad(vehicle.Position2f);
        var laneDirection = projected.direction.Xy;
        if (laneDirection.IsZero)
        {
            return;
        }

        // Correct only the lateral offset. The lane query clamps its result to
        // the finite trajectory, so copying the complete projected XY position
        // would also pull a vehicle back along the lane at an exit and prevent
        // the native road-to-terrain transition from completing.
        var laneTangent = laneDirection.Normalized;
        var offsetToLane = projected.position.Xy - vehicle.Position2f;
        var longitudinalOffset = laneTangent *
            offsetToLane.Dot(laneTangent).ToFix32();
        var lateralOffset = offsetToLane - longitudinalOffset;
        var laneAlignedPosition = vehicle.Position2f + lateralOffset;

        // SetGroundPosition preserves tile-visit bookkeeping; SetPosition then
        // preserves the exact road height returned by the lane query.
        if (laneAlignedPosition != vehicle.Position2f)
        {
            SetGroundPosition(vehicle, laneAlignedPosition);
        }

        SetPosition(
            vehicle,
            laneAlignedPosition.ExtendHeight(projected.position.Height));

        // Native road driving projects only the height after applying regular
        // wheeled steering. Align both simulation and rendered road direction
        // with the lane tangent, then discard the steering command that would
        // otherwise push the vehicle sideways again on the next tick.
        SetDirection(vehicle, laneDirection.Angle.Normalized);
        CurrentRoadDirectionField.SetValue(vehicle, projected.direction);
        var steeringDriver =
            (SmoothDriver)SteeringDriverField.GetValue(vehicle);
        steeringDriver.Reset();
    }

    private bool IsDriverUsingBoostedBase(Vehicle vehicle)
    {
        if (!m_boostedSpeeds.TryGetValue(
                vehicle.Prototype.Id.Value,
                out var boosted))
        {
            return false;
        }

        var driver = (SmoothDriver)SpeedDriverField.GetValue(vehicle);
        return (Fix32)DriverMaxForwardSpeedBaseField.GetValue(driver) ==
            boosted.Value;
    }

    private static void SetDriverMaxForwardSpeed(
        Vehicle vehicle,
        RelTile1f maxForwardSpeed,
        Percent baseGameFactor)
    {
        var driver = (SmoothDriver)SpeedDriverField.GetValue(vehicle);
        DriverMaxForwardSpeedBaseField.SetValue(
            driver,
            maxForwardSpeed.Value);
        // DrivingEntity.SetSpeedFactor skips work when the percentage itself
        // did not change. Call the public driver method directly after changing
        // its base ceiling so the new maximum takes effect immediately.
        driver.SetSpeedFactor(baseGameFactor);
    }

    private static void ClampDriverToCurrentLimit(Vehicle vehicle)
    {
        var driver = (SmoothDriver)SpeedDriverField.GetValue(vehicle);
        // SetSpeed applies the just-restored effective forward/backward
        // limits. StartUpdate then aligns the saveable LastStepSpeed so a
        // vehicle cannot reload with one residual over-limit acceleration
        // sample after leaving a Ground Road.
        driver.SetSpeed(driver.Speed);
        driver.StartUpdate();
    }
}
