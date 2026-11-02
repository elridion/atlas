using System;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Shows at the cartography table that the atlas is being updated: golden motes drift up
    /// from the table top while the update runs, then burst outward and fade once it is done.
    /// Vanilla's own scribble and chime play at the moment of writing, before the atlas has
    /// caught up, so they could not tell the player it was still working. Purely local: the
    /// effect is a plain particle system, never networked. Its material is a copy of a vanilla
    /// effect's particle material, with a mote texture made here.
    /// </summary>
    internal sealed class TableSparkles
    {
        // The vanilla effect whose particle material (shader) is borrowed.
        private const string MaterialSource = "vfx_odin_despawn";
        // The least time the motes show, so a quick update still reads as one.
        private const float MinSeconds = 0.8f;
        private const float MoteRate = 35f;
        private const int BurstCount = 70;
        private const float BurstSpeed = 1.6f;
        private const float FadeSeconds = 2.5f;
        private static readonly Color s_gold = new Color(1f, 0.84f, 0.47f, 1f);
        private static readonly Color s_pale = new Color(1f, 0.95f, 0.78f, 1f);

        private static Material s_material;
        private static bool s_unavailable;

        private GameObject m_effect;
        private ParticleSystem m_particles;
        private float m_started;

        // Starts the motes over the table; a running effect restarts there.
        public void Begin(MapTable table)
        {
            Clear();
            if (!table || !Material()) return;
            try
            {
                Bounds bounds = TableBounds(table);
                m_effect = new GameObject("Atlas sparkles");
                m_effect.transform.position = new Vector3(bounds.center.x, bounds.max.y + 0.05f, bounds.center.z);
                m_particles = m_effect.AddComponent<ParticleSystem>();
                Setup(m_particles, bounds);
                m_particles.Play();
                m_started = Time.time;
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"The atlas could not show its table sparkles: {e.Message}");
                s_unavailable = true;
                Clear();
            }
        }

        // Each frame: once the update is no longer busy (and MinSeconds have passed), the motes
        // stop, a burst flies out, and the effect is removed after it has faded.
        public void Update(bool busy)
        {
            if (!m_effect || !m_particles) return;
            if (busy || Time.time - m_started < MinSeconds) return;
            m_particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            var burst = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = true,
            };
            for (int i = 0; i < BurstCount; i++)
            {
                Vector3 direction = UnityEngine.Random.onUnitSphere;
                direction.y = Mathf.Abs(direction.y) * 0.8f + 0.2f;
                burst.velocity = direction.normalized * BurstSpeed * UnityEngine.Random.Range(0.5f, 1f);
                burst.startLifetime = UnityEngine.Random.Range(0.8f, 1.5f);
                burst.startSize = UnityEngine.Random.Range(0.05f, 0.11f);
                burst.startColor = Color.Lerp(s_gold, s_pale, UnityEngine.Random.value);
                m_particles.Emit(burst, 1);
            }
            UnityEngine.Object.Destroy(m_effect, FadeSeconds);
            m_effect = null;
            m_particles = null;
        }

        public void Clear()
        {
            if (m_effect) UnityEngine.Object.Destroy(m_effect);
            m_effect = null;
            m_particles = null;
        }

        private static void Setup(ParticleSystem particles, Bounds bounds)
        {
            // A new system plays at once, and its duration cannot be set while it plays.
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = particles.main;
            main.loop = true;
            main.duration = 1f;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1f, 1.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.35f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
            main.startColor = new ParticleSystem.MinMaxGradient(s_gold, s_pale);
            main.gravityModifier = -0.04f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rateOverTime = MoteRate;

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(Mathf.Max(0.3f, bounds.size.x * 0.75f), 0.02f, Mathf.Max(0.3f, bounds.size.z * 0.75f));

            // Each mote eases in and out of sight.
            ParticleSystem.ColorOverLifetimeModule colour = particles.colorOverLifetime;
            colour.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0.8f, 0.6f), new GradientAlphaKey(0f, 1f) });
            colour.color = new ParticleSystem.MinMaxGradient(fade);

            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = s_material;
        }

        // The table's visible extent, so the motes rise from its top whatever its size.
        private static Bounds TableBounds(MapTable table)
        {
            Renderer[] renderers = table.GetComponentsInChildren<Renderer>();
            Bounds bounds = new Bounds(table.transform.position + Vector3.up, new Vector3(1f, 0f, 1f));
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (!renderer || renderer is ParticleSystemRenderer) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return bounds;
        }

        // A copy of a vanilla particle material with the mote texture, made once; null when the
        // vanilla effect is missing, which turns the sparkles off with one warning.
        private static Material Material()
        {
            if (s_material) return s_material;
            if (s_unavailable) return null;
            GameObject source = Jotunn.Managers.PrefabManager.Cache.GetPrefab<GameObject>(MaterialSource);
            ParticleSystemRenderer renderer = source ? source.GetComponentInChildren<ParticleSystemRenderer>(true) : null;
            if (!renderer || !renderer.sharedMaterial)
            {
                Jotunn.Logger.LogWarning($"The atlas found no {MaterialSource} particle material; the table shows no sparkles.");
                s_unavailable = true;
                return null;
            }
            var material = new Material(renderer.sharedMaterial) { name = renderer.sharedMaterial.name + "_atlas" };
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", Mote());
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", Color.white);
            s_material = material;
            return material;
        }

        // A soft round glow with a faint four-pointed glint, white so particles colour it.
        private static Texture2D Mote()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
            {
                name = "Atlas mote",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float glow = Mathf.Clamp01(1f - r);
                    glow *= glow;
                    float glint = Mathf.Clamp01(1f - Mathf.Abs(u) * 12f) * Mathf.Clamp01(1f - Mathf.Abs(v))
                        + Mathf.Clamp01(1f - Mathf.Abs(v) * 12f) * Mathf.Clamp01(1f - Mathf.Abs(u));
                    float a = Mathf.Clamp01(glow + glint * 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }
    }
}
