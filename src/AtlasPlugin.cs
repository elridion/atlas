using System.IO;
using BepInEx;
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

        private void Awake()
        {
            AddLocalizations();
            _harmony.PatchAll(typeof(AtlasPlugin).Assembly);
        }

        private void Update()
        {
            AtlasMap.Update();
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
