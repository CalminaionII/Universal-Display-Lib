using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// Material textures in the block atlas, readable from any thread. Inserting is GPU work and only
    /// allowed on the main thread, but placed windows are built on the tesselation thread - so a texture
    /// not in the atlas yet is queued for the main thread and the waiting blocks are redrawn once it is
    /// there. Normally nothing waits: every listed material is put in at LevelFinalize.
    /// </summary>
    public static class MaterialTextures
    {
        private static readonly ConcurrentDictionary<string, TextureAtlasPosition> Positions = new();
        /// <summary>Textures whose file does not exist (a wood mod not installed) - the shape's own texture is used.</summary>
        private static readonly ConcurrentDictionary<string, bool> Missing = new();
        private static readonly ConcurrentDictionary<string, ConcurrentBag<BlockPos>> Waiting = new();

        public static string KeyOf(string texture, IReadOnlyCollection<string> overlays) =>
            overlays is { Count: > 0 } ? texture + "++" + string.Join("++", overlays) : texture;

        public static void Clear()
        {
            Positions.Clear();
            Missing.Clear();
            Waiting.Clear();
        }

        /// <summary>True once a texture (with overlays) is known not to exist - the caller uses the shape's own instead.</summary>
        public static bool IsMissing(string texture, IReadOnlyCollection<string> overlays) =>
            !string.IsNullOrEmpty(texture) && Missing.ContainsKey(KeyOf(texture, overlays));

        public static void Prepare(ICoreClientAPI capi, IEnumerable<string> textures)
        {
            foreach (var t in textures) TryGet(capi, t, null, null, out _);
        }

        /// <summary>
        /// The atlas position of this texture (with overlays). False when the texture does not exist,
        /// or - off the main thread - when it is not in the atlas yet: then it is queued and
        /// <paramref name="redrawAt"/> is marked dirty once it is.
        /// </summary>
        public static bool TryGet(ICoreClientAPI capi, string texture, IReadOnlyCollection<string> overlays, BlockPos redrawAt, out TextureAtlasPosition position)
        {
            position = null;
            if (string.IsNullOrEmpty(texture)) return false;
            string key = KeyOf(texture, overlays);
            if (Positions.TryGetValue(key, out position)) return true;
            if (Missing.ContainsKey(key)) return false;

            var location = new AssetLocation(texture);
            if (overlays is not { Count: > 0 })
            {
                position = capi.BlockTextureAtlas[location]
                    ?? capi.BlockTextureAtlas[location.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png")];
                if (position != null) { Positions[key] = position; return true; }
            }

            if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)
            {
                Insert(capi, key, texture, overlays);
                return Positions.TryGetValue(key, out position);
            }

            var bag = Waiting.GetOrAdd(key, _ => new ConcurrentBag<BlockPos>());
            if (redrawAt != null) bag.Add(redrawAt.Copy());
            if (bag.Count <= 1)
            {
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    Insert(capi, key, texture, overlays);
                    if (Waiting.TryRemove(key, out var redraw))
                        foreach (var pos in redraw.Distinct()) capi.World.BlockAccessor.MarkBlockDirty(pos);
                }, "universaldisplaylib-materialtexture");
            }
            return false;
        }

        private static void Insert(ICoreClientAPI capi, string key, string texture, IReadOnlyCollection<string> overlays)
        {
            if (Positions.ContainsKey(key) || Missing.ContainsKey(key)) return;
            var all = new[] { texture }.Concat(overlays ?? Array.Empty<string>()).Select(t => new AssetLocation(t));
            if (all.Any(l => !capi.Assets.Exists(l.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"))))
            {
                Missing[key] = true;
                return;
            }
            var composite = new CompositeTexture(new AssetLocation(texture));
            if (overlays is { Count: > 0 }) composite.BlendedOverlays = overlays.Select(o => new BlendedOverlayTexture { Base = new AssetLocation(o) }).ToArray();
            capi.BlockTextureAtlas.GetOrInsertTexture(composite, out _, out var position);
            if (position != null) Positions[key] = position;
            else
            {
                Missing[key] = true;
                capi.Logger.Warning("[UniversalDisplayLib] Material texture {0} could not be put in the block atlas", key);
            }
        }
    }

    /// <summary>
    /// The texture source for a block with UDMaterials: each texture code the shape asks for gets its
    /// material's texture (with any overlays), a fixed texture, or else what it had before (the block's
    /// own / the shape's own). <see cref="Waiting"/> is true when a material texture was not in the
    /// atlas yet - the mesh built with it must not be kept.
    /// </summary>
    public sealed class MaterialTextureSource : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly BlockBehaviorUDMaterials materials;
        private readonly IReadOnlyDictionary<string, string> types;
        private readonly ITexPositionSource fallback;
        private readonly Shape shape;
        private readonly BlockPos redrawAt;

        public bool Waiting { get; private set; }

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public MaterialTextureSource(ICoreClientAPI capi, Block block, BlockBehaviorUDMaterials materials, IReadOnlyDictionary<string, string> types, Shape shape, BlockPos redrawAt)
        {
            this.capi = capi;
            this.materials = materials;
            this.types = types;
            this.shape = shape;
            this.redrawAt = redrawAt;
            fallback = capi.Tesselator.GetTextureSource(block);
        }

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                var (texture, overlays) = materials.Resolve(types, textureCode);
                if (texture != null)
                {
                    if (MaterialTextures.TryGet(capi, texture, overlays, redrawAt, out var position)) return position;
                    if (!MaterialTextures.IsMissing(texture, overlays)) Waiting = true;
                }
                return Fallback(textureCode);
            }
        }

        private TextureAtlasPosition Fallback(string textureCode)
        {
            var position = fallback?[textureCode];
            if (position != null && position != capi.BlockTextureAtlas.UnknownTexturePosition) return position;
            if (shape?.Textures != null && shape.Textures.TryGetValue(textureCode, out var location) && location != null
                && MaterialTextures.TryGet(capi, location.ToString(), null, redrawAt, out var own)) return own;
            return position ?? capi.BlockTextureAtlas.UnknownTexturePosition;
        }
    }
}
