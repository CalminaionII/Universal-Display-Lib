using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;

public partial class BEUniversalDisplay
{
    sealed record EmbeddedEmitter(string SlotId, Vec3f Position, AdvancedParticleProperties[] Properties);
    sealed record EmbeddedEffectsSnapshot(byte[] Light, EmbeddedEmitter[] Emitters);
    static readonly EmbeddedEffectsSnapshot NoEmbeddedEffects = new(null, Array.Empty<EmbeddedEmitter>());
    volatile EmbeddedEffectsSnapshot embeddedEffects = NoEmbeddedEffects;
    readonly object embeddedParticleLock = new();
    long embeddedEffectsListener;
    bool embeddedEffectsQueued, embeddedEffectsFailureLogged;
    public Action EmbeddedEffectsChanged { get; set; }

    // Only when something changed: content changes refresh straight away, so the tick only retries a refresh that met
    // an item not resolved yet, plus a 10 s safety pass.
    bool embeddedEffectsStale = true;
    int embeddedEffectsIdleTicks;
    const int EmbeddedEffectsSafetyTicks = 10;

    /// <summary>The host could not take the effects yet (still loading): try again on the next tick.</summary>
    internal void MarkEmbeddedEffectsStale() => embeddedEffectsStale = true;

    void StartEmbeddedEffects()
    {
        embeddedEffectsListener = RegisterGameTickListener(_ =>
        {
            if (embeddedEffectsStale || ++embeddedEffectsIdleTicks >= EmbeddedEffectsSafetyTicks) RefreshEmbeddedEffects();
        }, 1000);
        QueueEmbeddedEffects();
    }

    void QueueEmbeddedEffects()
    {
        if (!IsEmbedded || Api == null || _removed || embeddedEffectsQueued) return;
        embeddedEffectsQueued = true;
        Api.Event.EnqueueMainThreadTask(() =>
        {
            embeddedEffectsQueued = false;
            RefreshEmbeddedEffects();
        }, "udl-embedded-effects");
    }

    // Only called by the main-thread tick, interaction, or queued load task.
    // Lighting/particle workers consume the published snapshot below, never slots.
    public void RefreshEmbeddedEffects()
    {
        if (!IsEmbedded || Api == null || inv?.Api == null || _removed) return;
        embeddedEffectsIdleTicks = 0;
        try
        {
            inv.ResolveBlocksOrItems();
            var slots = SlotsSnapshot();
            byte[] light = null;
            bool unresolved = false;
            var emitters = new System.Collections.Generic.List<EmbeddedEmitter>();
            foreach (var kv in slots)
            {
                var stack = kv.Value?.Itemstack;
                if (stack == null) continue;
                if (stack.Collectible == null) { unresolved = true; continue; }
                var hsv = BEUniversalDisplay.StoredGlowIgnored(stack) ? null : stack.Collectible.GetLightHsv(Api.World.BlockAccessor, null, stack);
                if (hsv?.Length >= 3 && hsv[2] > (light?[2] ?? 0)) light = new[] { hsv[0], hsv[1], hsv[2] };
                if (capi == null) continue;
                var properties = ParticlesFor(kv.Value, CategoryForSlot(kv.Key), out var offset);
                var position = properties == null ? null : SlotEmitPosition(kv.Key, kv.Value, offset, slots);
                if (position == null) continue;
                emitters.Add(new(kv.Key, position, properties.Where(prop => prop != null).Select(prop =>
                {
                    var copy = prop.Clone();
                    copy.basePos = new Vec3d();
                    return copy;
                }).ToArray()));
            }
            embeddedEffects = new(light, emitters.ToArray());
            embeddedEffectsStale = unresolved;
            EmbeddedEffectsChanged?.Invoke();
        }
        catch (Exception e)
        {
            if (!embeddedEffectsFailureLogged) Api.Logger.Warning("[UniversalDisplayLib] Embedded effects refresh failed: {0}", e);
            embeddedEffectsFailureLogged = true;
        }
    }

    public void ClearEmbeddedEffects()
    {
        embeddedEffects = NoEmbeddedEffects;
        EmbeddedEffectsChanged?.Invoke();
    }

    void StopEmbeddedEffects()
    {
        if (embeddedEffectsListener != 0) UnregisterGameTickListener(embeddedEffectsListener);
        embeddedEffectsListener = 0;
        EmbeddedEffectsChanged = null;
        embeddedEffects = NoEmbeddedEffects;
    }

    void SpawnEmbeddedParticles(IAsyncParticleManager manager, BlockPos pos, float wind)
    {
        var snapshot = embeddedEffects;
        lock (embeddedParticleLock)
            foreach (var emitter in snapshot.Emitters)
                foreach (var prop in emitter.Properties)
                {
                    // a levitating item's flame bobs and turns with it (LevitatingDisplay.cs FollowHover)
                    var at = FollowHover(emitter.SlotId, emitter.Position);
                    prop.WindAffectednesAtPos = wind;
                    prop.basePos.Set(pos.X + at.X, pos.InternalY + at.Y, pos.Z + at.Z);
                    manager.Spawn(prop);
                }
    }
}
