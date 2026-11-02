using HarmonyLib;

namespace Atlas
{
    /// <summary>
    /// The cartography table in worlds without a map: "Record discoveries" also updates the atlas,
    /// "Read map" opens vanilla's interactive map, where pins can be placed. Vanilla's own table
    /// behaviour (sharing explored land through the table) runs first and is left as it is.
    /// </summary>
    [HarmonyPatch]
    internal static class TablePatches
    {
        // Vanilla OnWrite calls this with showMessage false before writing; only a real read counts.
        [HarmonyPatch(typeof(MapTable), "OnRead", typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool))]
        [HarmonyPostfix]
        private static void OnRead(MapTable __instance, Humanoid user, ItemDrop.ItemData item, bool showMessage)
        {
            if (!showMessage || item != null || !Game.m_noMap || !user || user != Player.m_localPlayer) return;
            if (!PrivateArea.CheckAccess(__instance.transform.position, 0f, flash: false)) return;
            InteractiveMap.Show();
        }

        // Vanilla returns true when it wrote the player's map to the table (and also, without
        // writing, when the player lacks access, which the check below rules out).
        [HarmonyPatch(typeof(MapTable), "OnWrite")]
        [HarmonyPostfix]
        private static void OnWrite(MapTable __instance, Humanoid user, ItemDrop.ItemData item, bool __result)
        {
            if (!__result || item != null || !Game.m_noMap || !user || user != Player.m_localPlayer) return;
            if (!PrivateArea.CheckAccess(__instance.transform.position, 0f, flash: false)) return;
            AtlasMap.Record(__instance);
        }

        // Vanilla asks this before moving the camera, attacking, capturing the mouse or opening the
        // menu; an open atlas counts as an open map.
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.IsOpen))]
        [HarmonyPostfix]
        private static void IsOpen(ref bool __result)
        {
            if (!__result && AtlasMap.BlocksGame) __result = true;
        }
    }
}
