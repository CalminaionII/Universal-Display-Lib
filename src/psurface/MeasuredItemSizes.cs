using System;
using System.Collections.Concurrent;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>A client's measurement of an item's drawn size on one display category, for the server.</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class MeasuredItemSizePacket
    {
        public string Key;
        public float Width, Height, Length;
        /// <summary>Measured from the ordinary model drawn while the item's own was still being built.</summary>
        public bool StandIn;
    }

    /// <summary>
    /// MEASURED FOOTPRINTS (Calm, 2026-10-07: "Add it in now so I can see if it works") - an item's size for FITTING and
    /// STACKING is taken from what is drawn, so a patch no longer needs to write one.
    ///
    /// The client measures the item's mesh with its display transform applied (the exact mesh it draws on that category):
    /// width and length are its extent across, height is how high its top sits above the surface - the stack pitch. The
    /// server never draws items, so each client sends what it measured, once per item and category; the server keeps the
    /// first it is sent. Until the server has one, it uses the written size (or the default), as before.
    ///
    /// Not measured - the written size stays:
    ///   * an entry with "outline": "size" (crossed-plane plants, whose model is a whole block of see-through sheet),
    ///   * chiselled blocks, which already measure their own size from their voxels,
    ///   * a size being tuned in .udedit (that override is applied after this).
    /// An item still needs a displayable entry to be placeable at all; only the size can be left out of it.
    ///
    /// STAND-IN SIZES (2026-10-09, antlered figurines that "forget the box"): an item that builds its own mesh
    /// (IContainedMeshSource) can return null while that mesh is not ready, and the display draws the item's ordinary
    /// model meanwhile. A size measured from that is kept as a STAND-IN: used until the real mesh is measured, which then
    /// replaces it on both sides, once. A real size is never replaced, and an item whose own mesh never comes keeps the
    /// stand-in, which is then simply what it looks like.
    /// </summary>
    public static class MeasuredItemSizes
    {
        static readonly ConcurrentDictionary<string, Size3f> sizes = new();
        static readonly ConcurrentDictionary<string, byte> sent = new();
        static readonly ConcurrentDictionary<string, byte> standIns = new();

        /// <summary>Clear on world start - a static outlives the world it describes.</summary>
        public static void ForgetAll()
        {
            sizes.Clear();
            sent.Clear();
            standIns.Clear();
        }

        static string KeyOf(ItemSlot slot, string category)
        {
            CollectibleObject c = slot?.Itemstack?.Collectible;
            if (c?.Code == null) return null;
            // a worn-shape item's look (WornShapeMesh): a backpack with pouches gets its own footprint
            string look = WornShapeMesh.LookKey(slot.Itemstack);
            return c.Code + "|" + (BlockBehaviorDisplaySurfaces.TypedKeyOf(slot) ?? "") + (look != null ? "|" + look : "")
                   + "|" + (category ?? BlockBehaviorDisplaySurfaces.DefaultDisplayCategory);
        }

        /// <summary>Whether this item's size on this category comes from its model at all.</summary>
        public static bool Applies(ItemSlot slot, string category)
        {
            if (slot?.Itemstack?.Collectible == null) return false;
            if (slot.Itemstack.Block is BlockMicroBlock) return false;
            return !BlockBehaviorDisplaySurfaces.OutlineFromSize(slot, category);
        }

        /// <summary>Both sides: the measured size, if one is known.</summary>
        public static bool TryGet(ItemSlot slot, string category, out Size3f size)
        {
            size = null;
            if (sizes.IsEmpty || !Applies(slot, category)) return false;
            string key = KeyOf(slot, category);
            return key != null && sizes.TryGetValue(key, out size);
        }

        /// <summary>
        /// Client: measure <paramref name="mesh"/> - the item's display mesh for <paramref name="category"/>, transform applied,
        /// before it is placed in a slot - and remember it. Returns true when this is a NEW size (callers redraw). Any thread.
        /// <paramref name="standIn"/>: the mesh is the ordinary model drawn while the item's own is not built yet.
        /// </summary>
        public static bool Measure(ICoreClientAPI capi, ItemSlot slot, string category, MeshData mesh, bool standIn = false)
        {
            if (capi == null || mesh?.xyz == null || mesh.VerticesCount == 0 || !Applies(slot, category)) return false;
            string key = KeyOf(slot, category);
            if (key == null) return false;
            // known already - unless what is known is a stand-in and this is the real thing
            if (sizes.ContainsKey(key) && (standIn || !standIns.ContainsKey(key))) return false;

            Cuboidf b = BEUniversalDisplay.MeshBounds(mesh, Identity);
            if (b == null) return false;
            var size = new Size3f(Clean((b.X2 - b.X1) * 16f), Clean(b.Y2 * 16f), Clean((b.Z2 - b.Z1) * 16f));
            if (!Store(key, size, standIn)) return false;

            if (sent.TryAdd(standIn ? key + "|standin" : key, 0))
            {
                var packet = new MeasuredItemSizePacket { Key = key, Width = size.Width, Height = size.Height, Length = size.Length, StandIn = standIn };
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    UniversalDisplayLibMod.ClientChannel?.SendPacket(packet);
                    BlockBehaviorDisplaySurfaces.BumpGeneration();
                }, "universaldisplaylib-measuredsize");
            }
            return true;
        }

        /// <summary>
        /// Server: a client's measurement. The first one for an item and category stands - except a stand-in, which the
        /// first real one replaces.
        /// </summary>
        public static void Receive(IServerPlayer player, MeasuredItemSizePacket packet)
        {
            if (packet?.Key == null || packet.Key.Length > 400) return;
            if (!Sane(packet.Width) || !Sane(packet.Height) || !Sane(packet.Length)) return;
            // A key a client sends must name an item this server actually has, and there is a ceiling on how many
            // are kept (2026-10-22): otherwise a buggy or hostile client could fill the server's memory with made-up
            // keys, each one making every loaded display rebuild its boxes.
            if (sizes.Count >= MaxServerSizes && !sizes.ContainsKey(packet.Key)) return;
            if (!NamesRealItem(player?.Entity?.World, packet.Key)) return;
            if (Store(packet.Key, new Size3f(packet.Width, packet.Height, packet.Length), packet.StandIn))
            {
                BlockBehaviorDisplaySurfaces.BumpGeneration();
                BEUniversalDisplay.ItemSizesChanged();   // a taller stack may now reach the space above (fillers)
            }
        }

        /// <summary>
        /// Both sides: keep a size. A stand-in only fills an empty key; a real size fills one or replaces a stand-in. ONE LOCKED
        /// STEP (2026-10-10): the size and its stand-in mark were two writes, so a real size arriving between them was lost.
        /// </summary>
        static bool Store(string key, Size3f size, bool standIn)
        {
            lock (storeLock)
            {
                if (standIn)
                {
                    if (!sizes.TryAdd(key, size)) return false;
                    standIns[key] = 0;
                    return true;
                }
                if (standIns.TryRemove(key, out _))
                {
                    sizes[key] = size;
                    return true;
                }
                return sizes.TryAdd(key, size);
            }
        }

        static readonly object storeLock = new();

        /// <summary>The most item sizes the server keeps from clients - far more than any modpack's displayable items.</summary>
        const int MaxServerSizes = 20000;

        /// <summary>Whether a key (KeyOf: the item code first, then "|...") starts with the code of a real item or block.</summary>
        static bool NamesRealItem(IWorldAccessor world, string key)
        {
            if (world == null) return false;
            int bar = key.IndexOf('|');
            if (bar <= 0) return false;
            var code = new AssetLocation(key.Substring(0, bar));
            return world.GetItem(code) != null || world.GetBlock(code) != null;
        }

        static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        // A tenth of a voxel is finer than anything a footprint can show; no zero or runaway sizes
        static float Clean(float voxels) => Math.Clamp(MathF.Round(voxels * 10f) / 10f, 0.5f, 48f);

        static bool Sane(float v) => !float.IsNaN(v) && v >= 0.5f && v <= 48f;
    }
}
