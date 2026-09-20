using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Events;
using Content.Shared.Mobs.Systems;
using Content.Shared.Rejuvenate;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Shared._RMC14.Medical.Pain;

public sealed partial class PainSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PainComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<PainComponent, AfterAutoHandleStateEvent>(OnPainState);
        SubscribeLocalEvent<PainComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<PainComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<PainComponent, BeforeAlertSeverityCheckEvent>(OnAlertSeverityCheck);
        SubscribeLocalEvent<PainComponent, RejuvenateEvent>(OnRejuvenate);
    }

    /// <summary>
    /// Add a new <see cref="PainModifier"/> to <paramref name="ent"/>'s <see cref="PainComponent.PainModifiers"/>,
    /// to be removed when its <see cref="PainModifier.ExpireAt"/> time is reached.
    /// </summary>
    public void AddPainModifier(Entity<PainComponent?> ent, PainModifier mod)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        ent.Comp.PainModifiers.Add(mod);
        DirtyField(ent, ent.Comp, nameof(PainComponent.PainModifiers));
    }

    /// <inheritdoc cref="AddPainModifier(Entity{PainComponent?}, PainModifier)"/>
    public void AddPainModifier(Entity<PainComponent?> ent, TimeSpan duration, FixedPoint2 effectStrength, PainModifierType type)
    {
        var expireAt = _timing.CurTime + duration;
        var mod = new PainModifier(expireAt, effectStrength, type);
        AddPainModifier(ent, mod);
    }

    /// <summary>
    /// Remove all currently active <see cref="PainModifier"/>s in <paramref name="ent"/>'s <see cref="PainComponent.PainModifiers"/>,
    /// regardless of whether they've reached their <see cref="PainModifier.ExpireAt"/> time or not.
    /// </summary>
    /// <remarks>
    /// Modifiers from painkillers or other <see cref="EntityEffect"/>s will automatically re-apply themselves the next tick.
    /// In order to prevent that, the source reagent/effect needs to be removed as well.
    /// </remarks>
    public void ClearPainModifiers(Entity<PainComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp))
            return;

        ent.Comp.PainModifiers.Clear();
        DirtyField(ent, ent.Comp, nameof(PainComponent.PainModifiers));
    }

    private void OnInit(Entity<PainComponent> ent, ref ComponentInit args)
    {
        DebugTools.Assert(ent.Comp.PainLevels.SequenceEqual(ent.Comp.PainLevels.OrderBy(level => level.Threshold)),
            $"{nameof(PainComponent)}.{nameof(PainComponent.PainLevels)} entries must be written in order of their thresholds. (Low -> High)");
    }

    /// <summary>
    /// Used to force an update of the client-side pain overlay vignette and health alert whenever
    /// <see cref="PainComponent.CurrentPainLevelIdx"/> updates.
    /// </summary>
    /// <remarks>
    /// The pain overlay is <i>usually</i> updated by the <see cref="MobThresholdChecked"/> event whenever the player's damage changes,
    /// but <see cref="PainComponent.CurrentPainLevelIdx"/> is specifically designed to lag behind by a few seconds.<br/>
    /// The <see cref="PainLevelChangedEvent"/> in here works to keep it up-to-date as the pain level slowly increases or decreases.
    /// </remarks>
    private void OnPainState(Entity<PainComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (ent.Comp.PreviousPainLevelIdx != ent.Comp.CurrentPainLevelIdx)
        {
            var painComp = ent.Comp;
            var ev = new PainLevelChangedEvent(ent, painComp.PreviousPainLevelIdx, painComp.CurrentPainLevelIdx);
            RaiseLocalEvent(ent, ref ev, true);
            painComp.PreviousPainLevelIdx = painComp.CurrentPainLevelIdx;

            if (painComp.CurrentPainLevelIdx <= _alerts.GetMaxSeverity(painComp.Alert))
                _alerts.ShowAlert(ent, painComp.Alert, (short)painComp.CurrentPainLevelIdx);
        }
    }

    private void OnRejuvenate(Entity<PainComponent> ent, ref RejuvenateEvent args)
    {
        ent.Comp.PerceivedPain = 0;
        ent.Comp.CurrentPainLevelIdx = 0;
        ent.Comp.PainModifiers.Clear();
        DirtyFields(ent, ent.Comp, null,
            nameof(PainComponent.PerceivedPain),
            nameof(PainComponent.CurrentPainLevelIdx),
            nameof(PainComponent.PainModifiers));
    }

    private void OnAlertSeverityCheck(Entity<PainComponent> ent, ref BeforeAlertSeverityCheckEvent args)
    {
        if (args.CurrentAlert == ent.Comp.Alert)
        {
            args.Severity = Math.Min((short)ent.Comp.CurrentPainLevelIdx, _alerts.GetMaxSeverity(ent.Comp.Alert));
            args.CancelUpdate = true;
        }
    }

    private void OnDamageChanged(Entity<PainComponent> ent, ref DamageChangedEvent args)
    {
        var painComp = ent.Comp;
        var damage = args.Damageable.Damage;
        var newPainValue = FixedPoint2.Zero;

        foreach (var (groupId, value) in ent.Comp.DamageGroupPainMultipliers)
            newPainValue += GetDamageGroupPain(damage, groupId, value);

        if (painComp.BasePain != newPainValue)
        {
            painComp.BasePain = newPainValue;
            DirtyField(ent, ent.Comp, nameof(PainComponent.BasePain));
        }

        FixedPoint2 GetDamageGroupPain(DamageSpecifier damage, ProtoId<DamageGroupPrototype> damageGroup, FixedPoint2 painMultiplier)
        {
            if (painMultiplier != 0 &&
                _prototypes.TryIndex(damageGroup, out var groupPrototype) &&
                damage.TryGetDamageInGroup(groupPrototype, out var groupDamage))
            {
                return groupDamage * painMultiplier;
            }
            return FixedPoint2.Zero;
        }
    }

    private void OnMobStateChanged(Entity<PainComponent> ent, ref MobStateChangedEvent args)
    {
        // Going from *not* dead to dead.
        if (args.NewMobState == MobState.Dead)
        {
            // Clear out all of their (relevant) `PainComponent` vars, just for the sake of preventing weird edge case behaviour.
            // If the user gets revived then they all repopulate themselves automatically.
            ent.Comp.PerceivedPain = 0;
            ent.Comp.CurrentPainLevelIdx = 0;
            ent.Comp.PainModifiers.Clear();
            DirtyFields(ent, ent.Comp, null,
                nameof(PainComponent.PerceivedPain),
                nameof(PainComponent.CurrentPainLevelIdx),
                nameof(PainComponent.PainModifiers));
        }
        // Going from dead to *not* dead.
        else if (args.OldMobState == MobState.Dead)
        {
            // Jump the vars back over to where they would have been if the system hadn't stopped updating after they died.
            // This *does* happen automatically in `Update()`, but that only moves `CurrentPainLevelIdx` one step at a time.
            // Setting it here is just to skip the wait time.
            UpdatePerceivedPain(ent);
            ent.Comp.CurrentPainLevelIdx = GetHighestValidPainLevelIdx(ent);
            DirtyField(ent, ent.Comp, nameof(PainComponent.CurrentPainLevelIdx));

            // Also force an update of the pain overlay.

        }
    }

    private void UpdatePerceivedPain(Entity<PainComponent> ent)
    {
        var maxPainReductionModifierStrength = FixedPoint2.Zero;
        var painIncrease = FixedPoint2.Zero;
        foreach (var modifier in ent.Comp.PainModifiers)
        {
            switch (modifier.Type)
            {
                case PainModifierType.PainReduction:
                    maxPainReductionModifierStrength = FixedPoint2.Max(modifier.EffectStrength, maxPainReductionModifierStrength);
                    break;
                case PainModifierType.PainIncrease:
                    painIncrease += modifier.EffectStrength;
                    break;
            }
        }

        var painWithIncrease = ent.Comp.BasePain + painIncrease;
        // Pain reduction effectiveness linearly decreases as the pain goes up
        var newPainReduction = FixedPoint2.Max(0, -painWithIncrease * ent.Comp.PainReductionDecreaseRate + maxPainReductionModifierStrength);
        var newPainPercentage = FixedPoint2.Clamp(painWithIncrease - newPainReduction, 0, 100);

        if (newPainPercentage != ent.Comp.PerceivedPain)
        {
            ent.Comp.PerceivedPain = newPainPercentage;
            DirtyField(ent, ent.Comp, nameof(PainComponent.PerceivedPain));
        }
    }

    /// <summary>
    /// Get the index of the highest <see cref="PainLevel"/> in <paramref name="ent"/>'s
    /// <see cref="PainComponent.PainLevels"/> where <c>PainLevel.Threshold &lt;= ent.Comp.PerceivedPain</c>.
    /// </summary>
    /// <seealso cref="PainComponent.PerceivedPain"/>
    private static int GetHighestValidPainLevelIdx(Entity<PainComponent> ent)
    {
        return ent.Comp.PainLevels.FindLastIndex(level => level.Threshold <= ent.Comp.PerceivedPain);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var time = _timing.CurTime;
        var painQuery = EntityQueryEnumerator<PainComponent>();
        while (painQuery.MoveNext(out var uid, out var pain))
        {
            if (time < pain.NextUpdateTime || _mobState.IsDead(uid))
                continue;

            pain.NextUpdateTime = time + pain.UpdateRate;
            DirtyField(uid, pain, nameof(PainComponent.NextUpdateTime));

            if (pain.BasePain == 0 &&
                pain.PerceivedPain == 0 &&
                pain.CurrentPainLevelIdx == 0 &&
                pain.PainModifiers.Count == 0)
            {
                // Nothing to process!
                continue;
            }

            // Remove any expired modifiers.
            // (expire timings get messy on client due to the `EntityEffect` problem mentioned below, so server only here)
            if (_net.IsServer && pain.PainModifiers.RemoveAll(mod => time > mod.ExpireAt) != 0)
                DirtyField(uid, pain, nameof(PainComponent.PainModifiers));

            // Update the pain felt by the player.
            var uidEntity = new Entity<PainComponent>(uid, pain);
            UpdatePerceivedPain(uidEntity);

            if (time >= pain.NextPainLevelUpdateTime)
            {
                pain.NextPainLevelUpdateTime = time + pain.PainLevelUpdateRate;
                DirtyField(uid, pain, nameof(PainComponent.NextPainLevelUpdateTime));

                // Get the highest level in `PainLevels` whose threshold has been passed by `PerceivedPain`.
                var highestPainLevelIdx = GetHighestValidPainLevelIdx(uidEntity);

                /* todo: new problem discovered
                 * When a modifier is added (probably other things too), if the server updates
                 * the pain level first in here before the client actually recieves that the modifier has been added,
                 * by the time the client gets sent the update `CurrentPainLevelIdx` will have already been set, so the client
                 * never calls `SetCurrentPainLevelIdx()` and the overlay doesn't update.
                 * (remove this comment when it's fixed)
                 */

                // Move `currentPainLevelIdx` towards `highestPainLevelIdx` by one step.
                if (highestPainLevelIdx > pain.CurrentPainLevelIdx)
                    pain.CurrentPainLevelIdx++;
                else if (highestPainLevelIdx < pain.CurrentPainLevelIdx)
                    pain.CurrentPainLevelIdx--;
                DirtyField(uid, pain, nameof(PainComponent.CurrentPainLevelIdx));
            }

            // Server-side only from this point because `EntityEffect`s are seemingly unable to be serialized over to the client.
            if (_net.IsClient)
                continue;

            // Trigger any effects defined for this pain level.
            var currentEffectList = pain.PainLevels[pain.CurrentPainLevelIdx].LevelEffects;
            if (currentEffectList.Count == 0)
                continue;

            var args = new EntityEffectBaseArgs(uid, EntityManager);
            foreach (var effect in currentEffectList)
            {
                if (!effect.ShouldApply(args, _random))
                    continue;

                effect.Effect(args);
            }
        }
    }
}
