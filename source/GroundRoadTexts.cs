using Mafi.Localization;
using MultiLangLib;

namespace GroundRoads;

internal static class GroundRoadTexts
{
    private const string ModId = "GroundRoads";

    public static string Get(string textId) => Lang.Get(ModId, textId);

    public static LocStrFormatted Localized(string textId) =>
        Lang.Localized(ModId, textId);
}
