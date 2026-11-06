using HarmonyLib;

namespace Atlas
{
    /// <summary>
    /// Opens vanilla's large map in a world without a map. Vanilla refuses any map mode while
    /// Game.m_noMap is set, so the flag is lifted for the one call; once open, vanilla keeps the
    /// map up and closes it on the map key or Escape as usual, and its own nomap check sends it
    /// back to no map at all.
    /// </summary>
    internal static class InteractiveMap
    {
        // Vanilla ignores map clicks until this runs out, so the key press that read the table does
        // not also place a pin.
        private const float InputDelay = 1f;

        private static readonly AccessTools.FieldRef<Minimap, float> s_inputDelay =
            AccessTools.FieldRefAccess<Minimap, float>("inputDelay");

        public static void Show()
        {
            Minimap minimap = Minimap.instance;
            if (!minimap || !Player.m_localPlayer || AtlasMap.BlocksGame) return;
            bool noMap = Game.m_noMap;
            try
            {
                Game.m_noMap = false;
                s_inputDelay(minimap) = InputDelay;
                minimap.SetMapMode(Minimap.MapMode.Large);
                AtlasMap.BeginEdit();
            }
            finally
            {
                Game.m_noMap = noMap;
            }
        }
    }
}
