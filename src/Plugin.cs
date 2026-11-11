using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SymmetricalBuildingPieces
{
    public enum PlaneMode
    {
        Pivot,        // mirror across the piece's origin (where snapping is centred)
        BoundsCenter, // mirror across the middle of the mesh's thickness
    }

    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "bhimajenkins.symmetricalbuildingpieces";
        public const string Name = "Symmetrical Building Pieces";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;
        internal static string PluginPath;
        internal static ConfigEntry<string> PieceList;
        internal static ConfigEntry<string> NameSuffix;
        internal static ConfigEntry<PlaneMode> Plane;
        internal static ConfigEntry<float> PlaneOffset;

        private const string DefaultPieces =
            "woodwall, wood_wall_half, wood_wall_quarter, " +
            "scale_wall_2x2, scale_halfwall_1x2, scale_quarterwall_1x1, " +
            "scale_wall_roof_26:flip, scale_wall_roof_45:flip, scale_wall_roof_67, " +
            "scale_wall_roof_26_upsidedown_flipped, scale_wall_roof_45_upsidedown_flipped, scale_wall_roof_67_upsidedown_flipped, " +
            "ashwood_quarterwall_1x1:flip, ashwood_halfwall_1x2, ashwood_wall_2x2, " +
            "stave_wall_2x2";

        private void Awake()
        {
            Log = Logger;
            PluginPath = Info.Location;

            PieceList = Config.Bind("General", "Pieces", DefaultPieces,
                "Comma-separated vanilla prefab names to make symmetrical copies of.\n" +
                "Add ':+' or ':-' to force which face is kept, or ':flip' for the opposite of auto (default 'auto' keeps the flatter face).\n" +
                "Example: woodwall:+, wood_wall_half:-\n" +
                "Changing a side rebuilds placed pieces live; newly added names apply on next world load.");
            NameSuffix = Config.Bind("General", "NameSuffix", " (Symmetrical)",
                "Text added after the vanilla name in the build menu. Takes effect on restart.");
            Plane = Config.Bind("Advanced", "MirrorPlane", PlaneMode.Pivot,
                "Where the mirror plane sits. Pivot = piece origin. BoundsCenter = middle of the thickness.");
            PlaneOffset = Config.Bind("Advanced", "MirrorPlaneOffset", 0f,
                "Extra offset (metres) of the mirror plane along the wall's thickness axis.");

            PieceList.SettingChanged += (_, __) => PieceRegistry.RebuildAll();
            Plane.SettingChanged += (_, __) => PieceRegistry.RebuildAll();
            PlaneOffset.SettingChanged += (_, __) => PieceRegistry.RebuildAll();

            new Harmony(Guid).PatchAll();
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    internal static class ZNetSceneAwakePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix() => PieceRegistry.TrySetup();
    }

    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    internal static class ObjectDBAwakePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix() => PieceRegistry.TrySetup();
    }

    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class TerminalPatch
    {
        private static void Postfix()
        {
            new Terminal.ConsoleCommand("sbp_dump",
                "[filter] - log Hammer pieces whose prefab or display name contains filter (default 'wall'), with mesh info",
                args =>
                {
                    string filter = args.Length > 1 ? args[1] : "wall";
                    int n = PieceRegistry.Dump(filter);
                    args.Context?.AddString($"SBP: {n} pieces written to BepInEx/LogOutput.log");
                });

            new Terminal.ConsoleCommand("sbp_rebuild",
                "- re-read the SBP config and rebuild all symmetrical meshes",
                args =>
                {
                    Plugin.PieceList.ConfigFile.Reload();
                    PieceRegistry.RebuildAll();
                    args.Context?.AddString("SBP: rebuilt");
                });
        }
    }
}