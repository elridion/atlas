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
        public const string PluginVersion = "0.1.0";

        private const string TranslationPrefix = "Atlas.translations.";

        private readonly Harmony _harmony = new Harmony(PluginGuid);

        // Which of the game's own map marks the atlas shows. The player's own pins always show.
        public static ConfigEntry<bool> BedPin;
        public static ConfigEntry<bool> DeathPin;
        public static ConfigEntry<bool> BossPins;
        public static ConfigEntry<bool> TraderPins;
        public static ConfigEntry<bool> HildirPins;
        public static ConfigEntry<bool> PlacePins;

        private void Awake()
        {
            BedPin = Config.Bind("Pins", "BedPin", true, "Show your bed (spawn point) on the atlas.");
            DeathPin = Config.Bind("Pins", "DeathPin", true, "Show where you last died on the atlas.");
            BossPins = Config.Bind("Pins", "BossPins", true, "Show the boss altars a Vegvisir revealed on the atlas.");
            TraderPins = Config.Bind("Pins", "TraderPins", true, "Show the traders (Haldor, Hildir, the Bog Witch) on the atlas.");
            HildirPins = Config.Bind("Pins", "HildirPins", true, "Show the dungeons of Hildir's requests on the atlas.");
            PlacePins = Config.Bind("Pins", "PlacePins", true, "Show other places the game marks itself, such as the Forge of Potential or other mods' locations, on the atlas.");
            // Pins drawn into the sheet change once it is painted again; markers the next time the
            // atlas opens.
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
