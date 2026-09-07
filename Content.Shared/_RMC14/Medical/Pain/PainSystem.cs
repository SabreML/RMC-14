using System.Linq;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;
using Content.Shared.EntityEffects;
using Robust.Shared.Timing;
using Robust.Shared.Random;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Alert;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mobs.Events;
using Content.Shared.Rejuvenate;

namespace Content.Shared._RMC14.Medical.Pain;

public sealed partial class PainSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    private static readonly ProtoId<DamageGroupPrototype> BruteGroup = "Brute";
    private static readonly ProtoId<DamageGroupPrototype> BurnGroup = "Burn";
    private static readonly ProtoId<DamageGroupPrototype> ToxinGroup = "Toxin";
    private static readonly ProtoId<DamageGroupPrototype> AirlossGroup = "Airloss";

    public override void Initialize()
    {
        SubscribeLocalEvent<PainComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<PainComponent, BeforeAlertSeverityCheckEvent>(OnAlertSeverityCheck);
        SubscribeLocalEvent<PainComponent, RejuvenateEvent>(OnRejuvenate);
    }

    // TODO: fix movement speed effect
    private void OnRejuvenate(Entity<PainComponent> ent, ref RejuvenateEvent args)
    {
        var pain = ent.Comp;
        pain.PainModifiers = [];
        pain.CurrentPain = 0;
        pain.CurrentPainPercentage = 0;
        pain.CurrentPainLevel = 0;
        Dirty(ent);

        _alerts.ShowAlert(ent, pain.Alert, 0);
    }

    private void OnAlertSeverityCheck(Entity<PainComponent> ent, ref BeforeAlertSeverityCheckEvent args)
    {
        if (args.CurrentAlert == ent.Comp.Alert)
        {
            args.Severity = Math.Min((short)ent.Comp.CurrentPainLevel, _alerts.GetMaxSeverity(ent.Comp.Alert));
            args.CancelUpdate = true;
        }
    }

    private void OnDamageChanged(Entity<PainComponent> ent, ref DamageChangedEvent args)
    {
        UpdateCurrentPain(ent, args.Damageable.Damage);
    }

    public void TryChangePainLevelTo(Entity<PainComponent?> ent, int level)
    {
        if (!Resolve(ent, ref ent.Comp))
            return;
        var painComp = ent.Comp;

        if (painComp.NextPainLevelUpdateTime > _timing.CurTime)
            return;

        painComp.NextPainLevelUpdateTime = _timing.CurTime + painComp.PainLevelUpdateRate;

        // `CompareTo()` returns either 1, 0, or -1, so this modifies it one step at a time.
        painComp.CurrentPainLevel += level.CompareTo(painComp.CurrentPainLevel);

        DirtyField(ent, ent.Comp, nameof(PainComponent.CurrentPainLevel));
        DirtyField(ent, ent.Comp, nameof(PainComponent.NextPainLevelUpdateTime));

        if (painComp.CurrentPainLevel <= _alerts.GetMaxSeverity(painComp.Alert))
            _alerts.ShowAlert(ent, painComp.Alert, (short)painComp.CurrentPainLevel);
    }
    public void AddPainModifier(Entity<PainComponent?> ent, TimeSpan duration, FixedPoint2 effectStrength, PainModifierType type)
    {
        var expireAt = _timing.CurTime + duration;
        var mod = new PainModifier(expireAt, effectStrength, type);
        AddPainModifier(ent, mod);
    }

    public void AddPainModifier(Entity<PainComponent?> ent, PainModifier mod)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        ent.Comp.PainModifiers.Add(mod);
        UpdateCurrentPainPercentage((ent, ent.Comp));
        DirtyField(ent, ent.Comp, nameof(PainComponent.PainModifiers));
    }

    private void UpdateCurrentPainPercentage(Entity<PainComponent> ent)
    {
        var maxPainReductionModificatorStrength = FixedPoint2.Zero;
        var painIncrease = FixedPoint2.Zero;
        var painIncreases = ent.Comp.PainModifiers.Where(mod => mod.Type == PainModifierType.PainIncrease);
        var painReductions = ent.Comp.PainModifiers.Where(mod => mod.Type == PainModifierType.PainReduction);
        // get max pain reduction, sum pain increase
        if (painIncreases.Any())
            painIncrease = painIncreases.Select(mod => mod.EffectStrength).Sum();
        if (painReductions.Any())
            maxPainReductionModificatorStrength = painReductions.Max(mod => mod.EffectStrength);

        var realCurrentPain = ent.Comp.CurrentPain + painIncrease;
        // Pain reduction effectiveness linearly decreases as the pain goes up
        var newPainReduction = FixedPoint2.Max(0, -realCurrentPain * ent.Comp.PainReductionDecreaseRate + maxPainReductionModificatorStrength);
        ent.Comp.CurrentPainPercentage = FixedPoint2.Clamp(realCurrentPain - newPainReduction, 0, 100);
        DirtyField(ent, ent.Comp, nameof(PainComponent.CurrentPainPercentage));
    }

    private void UpdateCurrentPain(Entity<PainComponent> ent, DamageSpecifier damage)
    {
        var painComp = ent.Comp;
        var newCurrentPain = FixedPoint2.Zero;

        newCurrentPain += GetDamageGroupPain(damage, BruteGroup, painComp.BrutePainMultiplier);
        newCurrentPain += GetDamageGroupPain(damage, BurnGroup, painComp.BurnPainMultiplier);
        newCurrentPain += GetDamageGroupPain(damage, ToxinGroup, painComp.ToxinPainMultiplier);
        newCurrentPain += GetDamageGroupPain(damage, AirlossGroup, painComp.AirlossPainMultiplier);

        if (painComp.CurrentPain != newCurrentPain)
        {
            painComp.CurrentPain = newCurrentPain;
            DirtyField(ent, ent.Comp, nameof(PainComponent.CurrentPain));
        }
    }

    private FixedPoint2 GetDamageGroupPain(DamageSpecifier damage, ProtoId<DamageGroupPrototype> damageGroup, FixedPoint2 painMultiplier)
    {
        if (_prototypes.TryIndex(damageGroup, out var groupPrototype) && damage.TryGetDamageInGroup(groupPrototype, out var groupDamage))
        {
            return groupDamage * painMultiplier;
        }
        return FixedPoint2.Zero;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var time = _timing.CurTime;
        var painQuery = EntityQueryEnumerator<PainComponent>();
        while (painQuery.MoveNext(out var uid, out var pain))
        {
            if (time < pain.NextEffectUpdateTime)
                continue;

            if (_mobState.IsDead(uid))
                continue;

            pain.NextEffectUpdateTime = time + pain.EffectUpdateRate;
            DirtyField(uid, pain, nameof(PainComponent.NextEffectUpdateTime));

            if (pain.PainModifiers.RemoveAll(mod => time > mod.ExpireAt) != 0)
                DirtyField(uid, pain, nameof(PainComponent.PainModifiers));

            UpdateCurrentPainPercentage((uid, pain));

            var painLevels = pain.PainLevels.OrderBy(level => level.Threshold).ToList(); // in case someone writes it in the wrong order
            var updatePainLevel = false;
            var expectedPainLevel = 0;

            for (var i = 0; i < painLevels.Count; i++)
            {
                if (painLevels[i].Threshold <= pain.CurrentPainPercentage)
                {
                    expectedPainLevel = i;  // update index to current element
                    updatePainLevel = true;
                }
                else
                {
                    break; // as list is sorted, no need to check further
                }
            }

            if (!updatePainLevel)
                continue;

            TryChangePainLevelTo((uid, pain), expectedPainLevel);

            if (painLevels.Count == 0)
                continue;

            var currentEffectList = painLevels[pain.CurrentPainLevel].LevelEffects;

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
