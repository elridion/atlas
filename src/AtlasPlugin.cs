using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;

namespace Atlas
{
    /// <summary>
    /// The plugin: loads translations, applies the patches and drives the atlas every frame.
    /// Client-side: the atlas is drawn and shown on this machine only, and the one thing saved is the
    /// last recorded exploration and pins on the character, so peers and servers without the mod can
    /// still connect.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    internal class AtlasPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "elridion.atlas";
        public const string PluginName = "Atlas";
        public const string PluginVersion = "0.1.1";

        private const string TranslationPrefix = "Atlas.translations.";

        private readonly Harmony _harmony = new Harmony(PluginGuid);

        // Which pins the atlas shows: the player's own, the game's own marks by kind, and any of
        // them out in unexplored land.
        public static ConfigEntry<bool> PlayerPins;
        public static ConfigEntry<bool> BedPin;
        public static ConfigEntry<bool> DeathPin;
        public static ConfigEntry<bool> BossPins;
        public static ConfigEntry<bool> TraderPins;
        public static ConfigEntry<bool> HildirPins;
        public static ConfigEntry<bool> PlacePins;
        public static ConfigEntry<bool> UnexploredPins;
        // The glimpse past the explored land (Paper.Fog).
        public static ConfigEntry<bool> Glimpse;

        private void Awake()
        {
            PlayerPins = Config.Bind("Pins", "PlayerPins", true, "Show the pins you placed yourself (the five icons of the pin menu) on the map.");
            BedPin = Config.Bind("Pins", "BedPin", true, "Show your bed (spawn point) on the map.");
            DeathPin = Config.Bind("Pins", "DeathPin", true, "Show where you last died on the map.");
            BossPins = Config.Bind("Pins", "BossPins", true, "Show the boss altars a Vegvisir revealed on the map.");
            TraderPins = Config.Bind("Pins", "TraderPins", true, "Show the traders (Haldor, Hildir, the Bog Witch) on the map.");
            HildirPins = Config.Bind("Pins", "HildirPins", true, "Show the dungeons of Hildir's requests on the map.");
            PlacePins = Config.Bind("Pins", "PlacePins", true, "Show the Sacrificial Stones, the Forge of Potential and the places other mods mark on the map.");
            UnexploredPins = Config.Bind("Pins", "UnexploredPins", true, "Show pins that lie outside the land you have explored. Off, only pins on explored land show.");
            Glimpse = Config.Bind("Fog", "Glimpse", true,
                "Show a band of the map on bare parchment past the land you have explored, and the game's own marks out in unexplored land (bosses, traders) drawn into the map with a glimpse round them. Off, the fog ends at the explored land and those marks show as markers on it.");
            // Markers change at once; pins drawn into the sheet once it is painted again in the
            // background, and the fog once it is laid again (AtlasMap.KeepFog).
            Config.SettingChanged += (sender, args) =>
            {
                AtlasView.PinsChanged();
                AtlasMap.PinsChanged();
            };
            AddLocalizations();
            _harmony.PatchAll(typeof(AtlasPlugin).Assembly);
        }

        // A failure in the atlas is logged once and switches it off for the session, rather than
        // throwing (and logging) every frame.
        private bool m_failed;

        private void Update()
        {
            if (m_failed) return;
            try
            {
                AtlasMap.Update();
            }
            catch (Exception e)
            {
                m_failed = true;
                Jotunn.Logger.LogError($"Atlas stopped after an error and stays off until the game restarts: {e}");
                AtlasMap.Stop();
            }
        }

        private void OnDestroy()
        {
            _harmony.UnpatchSelf();
        }

        // Each embedded assets/translations/<Language>.json is named after a Valheim language. Jotunn
        // falls back to English for languages and keys without a translation.
        private static void AddLocalizations()
        {
            var localization = LocalizationManager.Instance.GetLocalization();
            var assembly = typeof(AtlasPlugin).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(TranslationPrefix) || !resource.EndsWith(".json")) continue;
                string language = resource.Substring(TranslationPrefix.Length, resource.Length - TranslationPrefix.Length - ".json".Length);
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(resource)))
                    localization.AddJsonFile(language, reader.ReadToEnd());
            }
        }
    }
}
