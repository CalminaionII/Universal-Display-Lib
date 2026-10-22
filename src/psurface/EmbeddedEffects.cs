using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;

public partial class BEUniversalDisplay
{
    sealed record EmbeddedEmitter(Vec3f Position, AdvancedParticleProperties[] Properties);
    sealed record EmbeddedEffectsSnapshot(byte[] Light, EmbeddedEmitter[] Emitters);
    static readonly EmbeddedEffectsSnapshot NoEmbeddedEffects = new(null, Array.Empty<EmbeddedEmitter>());
    volatile EmbeddedEffectsSnapshot embeddedEffects = NoEmbeddedEffects;
    readonly object embeddedParticleLock = new();
    long embeddedEffectsListener;
    bool embeddedEffectsQueued, embeddedEffectsFailureLogged;
    public Action EmbeddedEffectsChanged { get; set; }

    // ONLY WHEN SOMETHING CHANGED (code check 2026-10-22): the 1 s tick used to redo every item's light and particles
    // each time. Content changes refresh straight away, so the tick now only retries a refresh that met an item not
    // resolved yet, plus a 10 s safety pass.
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
                var hsv = stack.Collectible.GetLightHsv(Api.World.BlockAccessor, null, stack);
                if (hsv?.Length >= 3 && hsv[2] > (light?[2] ?? 0)) light = new[] { hsv[0], hsv[1], hsv[2] };
                if (capi == null) continue;
                var properties = ParticlesFor(kv.Value, CategoryForSlot(kv.Key), out var offset);
                var position = properties == null ? null : SlotEmitPosition(kv.Key, kv.Value, offset, slots);
                if (position == null) continue;
                emitters.Add(new(position, properties.Where(prop => prop != null).Select(prop =>
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
                    prop.WindAffectednesAtPos = wind;
                    prop.basePos.Set(pos.X + emitter.Position.X, pos.InternalY + emitter.Position.Y, pos.Z + emitter.Position.Z);
                    manager.Spawn(prop);
                }
    }
}
