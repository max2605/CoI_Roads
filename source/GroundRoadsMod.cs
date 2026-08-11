using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.PathFinding;
using Mafi.Core.Prototypes;
using MultiLangLib;

namespace GroundRoads;

public sealed class GroundRoadsMod : IMod
{
    private GroundRoadVehicleEffects m_vehicleEffects;
    private GroundRoadToolbarRegistrator m_toolbarRegistrator;

    public ModManifest Manifest { get; }

    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig => Option<IConfig>.None;

    public ModJsonConfig JsonConfig { get; }

    public GroundRoadsMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
        Lang.RegisterMod(manifest.Id, manifest.RootDirectoryPath);
        Log.Info("GroundRoads: constructed");
    }

    public void RegisterPrototypes(ProtoRegistrator registrator)
    {
        registrator.RegisterData(new AsphaltProductionData());
        GroundRoadsData.Register(registrator);
    }

    public void RegisterDependencies(
        DependencyResolverBuilder depBuilder,
        ProtosDb protosDb,
        bool gameWasLoaded)
    {
        // Keep the game's original path finder as an internal delegate, while
        // replacing only the public service with the highway-aware wrapper.
        depBuilder.ClearRegistrations<IVehiclePathFinder>();
        depBuilder.RegisterDependency<HighwayTrafficDirector>().AsSelf();
        depBuilder.RegisterDependency<HighwayVehiclePathFinder>()
            .AsSelf()
            .AsAllInterfaces();
        depBuilder.RegisterDependency<GroundRoadModelFactory>().AsAllInterfaces();
        depBuilder.RegisterDependency<GroundRoadVehicleEffects>().AsSelf();
        depBuilder.RegisterDependency<HighwayJunctionPlacementValidator>()
            .AsAllInterfaces();
        depBuilder.RegisterDependency<GroundRoadDragController>().AsSelf();
        depBuilder.RegisterDependency<HighwayTIntersectionPlacementController>()
            .AsSelf();
        depBuilder.RegisterDependency<HighwayCrossIntersectionPlacementController>()
            .AsSelf();
        depBuilder.RegisterDependency<HighwayRoundaboutPlacementController>()
            .AsSelf();
        depBuilder.RegisterDependency<GroundRoadToolbarRegistrator>()
            .AsSelf()
            .AsAllInterfaces();
    }

    public void EarlyInit(DependencyResolver resolver)
    {
    }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        m_vehicleEffects = resolver.Resolve<GroundRoadVehicleEffects>();
        // IHotReloadUi registrations are not eagerly constructed for mods in
        // the gameplay resolver. Resolve the registrator explicitly so its
        // constructor can add the drag-line item to the toolbar.
        m_toolbarRegistrator =
            resolver.Resolve<GroundRoadToolbarRegistrator>();
        Log.Info(
            "GroundRoads: initialized. 4/8/16-tile resource-supplied " +
            "asphalt highways, static T4 passing-lane routing, " +
            "automatic traffic-director routing, Q/E elevation ramps with " +
            "concrete supports, " +
            "T/+ intersections, roundabouts, lane-bound driving, and road " +
            "bonuses are active; " +
            $"loadedSave={gameWasLoaded}.");
    }

    public void MigrateJsonConfig(
        VersionSlim savedVersion,
        Dict<string, object> savedValues)
    {
    }

    public void Dispose()
    {
        // Resolver-owned IDisposable dependencies are disposed by the game.
        // Calling Dispose here as well would remove the callback twice.
        m_vehicleEffects = null;
        m_toolbarRegistrator = null;
    }
}
