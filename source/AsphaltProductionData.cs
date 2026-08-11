using Mafi;
using Mafi.Base;
using Mafi.Base.Prototypes.Products;
using Mafi.Core.Factory.Machines;
using Mafi.Core.Factory.Recipes;
using Mafi.Core.Mods;
using Mafi.Core.Products;

namespace GroundRoads;

internal static class GroundRoadMaterialIds
{
    public static readonly ProductProto.ID Asphalt =
        Ids.Products.CreateId("GroundRoads_Asphalt");

    public static readonly RecipeProto.ID AsphaltMixing =
        new("GroundRoads_AsphaltMixing");
}

/// <summary>
/// Registers a storable asphalt mix and a 95/5 aggregate-to-binder recipe.
/// The existing industrial mixers already expose the loose/fluid/loose ports
/// needed for gravel, heavy oil, and the finished mix.
/// </summary>
internal sealed class AsphaltProductionData : IModData
{
    private const int AggregateQuantity = 19;
    private const int BinderQuantity = 1;
    private const int AsphaltOutputQuantity = 20;

    public void RegisterData(ProtoRegistrator registrator)
    {
        ProductBuilder.AddLooseProduct(
            registrator,
            GroundRoadMaterialIds.Asphalt,
            name: GroundRoadTexts.Get("product.asphalt.name"),
            material: Assets.Base.Products.Loose.SlagCrushed_mat,
            family: ProductBuilder.PileSmooth,
            icon: Assets.Base.Products.Icons.SlagCrushed_svg,
            isWaste: false,
            dumpByDefault: false,
            cannotBeStored: false,
            resourceColor: -1,
            pinToHomeScreen: false,
            isRecyclable: false,
            doNotTrackSource: false,
            translationComment:
                "Asphalt mix used to construct GroundRoads highways");

        var recipeBinding = registrator.RecipeProtoBuilder
            .Start(GroundRoadMaterialIds.AsphaltMixing)
            .AddInput(AggregateQuantity, Ids.Products.Gravel)
            .AddInput(BinderQuantity, Ids.Products.HeavyOil)
            .AddOutput(
                AsphaltOutputQuantity,
                GroundRoadMaterialIds.Asphalt)
            .BuildAndAdd();

        var protosDb = registrator.PrototypesDb;
        recipeBinding.BindTo(
            protosDb.GetOrThrow<MachineProto>(Ids.Machines.IndustrialMixer),
            20.Seconds());
        recipeBinding.BindTo(
            protosDb.GetOrThrow<MachineProto>(Ids.Machines.IndustrialMixerT2),
            10.Seconds());

        Log.Info(
            "GroundRoads: registered asphalt as 19 gravel + 1 heavy oil " +
            "=> 20 asphalt in industrial mixers I and II.");
    }
}
