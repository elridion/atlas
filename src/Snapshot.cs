using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// What the atlas showed when it was last updated at a cartography table: the explored map
    /// (the player's own and what the table shared) and the saved pins, kept per world in the
    /// character's custom data so the atlas survives logging out. The atlas is a record, not a live
    /// view: nothing explored or pinned afterwards shows until the next update.
    /// </summary>
    internal sealed class Snapshot
    {
        private const string ExploredKey = "elridion.atlas.explored.";
        private const string PinsKey = "elridion.atlas.pins.";
        private const int PinsVersion = 1;

        private static readonly AccessTools.FieldRef<Minimap, BitArray> s_explored =
            AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> s_exploredOthers =
            AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> s_pins =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

        public struct Pin
        {
            public Vector3 m_position;
            public string m_name;
            public Minimap.PinType m_type;
            public string m_icon;
            public bool m_checked;
        }

        public int m_size;
        public float m_pixelSize;
        public BitArray m_explored;
        public readonly List<Pin> m_pins = new List<Pin>();

        public bool IsExplored(float wx, float wz)
        {
            int x = Mathf.FloorToInt(wx / m_pixelSize) + m_size / 2;
            int z = Mathf.FloorToInt(wz / m_pixelSize) + m_size / 2;
            if (x < 0 || z < 0 || x >= m_size || z >= m_size) return false;
            return m_explored[z * m_size + x];
        }

        public static Snapshot Take(Minimap minimap)
        {
            var snapshot = new Snapshot
            {
                m_size = minimap.m_textureSize,
                m_pixelSize = minimap.m_pixelSize,
                m_explored = new BitArray(s_explored(minimap)),
            };
            snapshot.m_explored.Or(s_exploredOthers(minimap));
            foreach (Minimap.PinData pin in s_pins(minimap))
            {
                if (pin == null || !pin.m_save || !pin.m_icon) continue;
                snapshot.m_pins.Add(new Pin
                {
                    m_position = pin.m_pos,
                    m_name = pin.m_name ?? "",
                    m_type = pin.m_type,
                    m_icon = pin.m_icon.name,
                    m_checked = pin.m_checked,
                });
            }
            return snapshot;
        }

        public void Save(Player player, long worldUid)
        {
            var bytes = new byte[(m_explored.Length + 7) / 8];
            m_explored.CopyTo(bytes, 0);
            var explored = new ZPackage();
            explored.Write(m_size);
            explored.Write(m_pixelSize);
            explored.Write(Utils.Compress(bytes));
            player.m_customData[ExploredKey + worldUid] = Convert.ToBase64String(explored.GetArray());

            var pins = new ZPackage();
            pins.Write(PinsVersion);
            pins.Write(m_pins.Count);
            foreach (Pin pin in m_pins)
            {
                pins.Write(pin.m_position);
                pins.Write(pin.m_name);
                pins.Write((int)pin.m_type);
                pins.Write(pin.m_icon);
                pins.Write(pin.m_checked);
            }
            player.m_customData[PinsKey + worldUid] = Convert.ToBase64String(pins.GetArray());
        }

        // Null when this character never updated the atlas in this world.
        public static Snapshot Load(Player player, long worldUid)
        {
            if (!player.m_customData.TryGetValue(ExploredKey + worldUid, out string explored)) return null;
            try
            {
                var package = new ZPackage(Convert.FromBase64String(explored));
                var snapshot = new Snapshot { m_size = package.ReadInt(), m_pixelSize = package.ReadSingle() };
                snapshot.m_explored = new BitArray(Utils.Decompress(package.ReadByteArray())) { Length = snapshot.m_size * snapshot.m_size };
                if (player.m_customData.TryGetValue(PinsKey + worldUid, out string pins))
                {
                    package = new ZPackage(Convert.FromBase64String(pins));
                    if (package.ReadInt() == PinsVersion)
                    {
                        int count = package.ReadInt();
                        for (int i = 0; i < count; i++)
                        {
                            snapshot.m_pins.Add(new Pin
                            {
                                m_position = package.ReadVector3(),
                                m_name = package.ReadString(),
                                m_type = (Minimap.PinType)package.ReadInt(),
                                m_icon = package.ReadString(),
                                m_checked = package.ReadBool(),
                            });
                        }
                    }
                }
                return snapshot;
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"The saved atlas for world {worldUid} could not be read and starts blank: {e.Message}");
                return null;
            }
        }
    }
}
