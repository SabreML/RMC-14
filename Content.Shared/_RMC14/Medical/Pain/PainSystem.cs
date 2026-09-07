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
    private void OnRejuvenate(EntityUid uid, PainComponent pain, ref RejuvenateEvent args)
    {
        pain.PainModifiers = [];
        pain.CurrentPain = 0;
        pain.CurrentPainPercentage = 0;
        pain.CurrentPainLevel = 0;
        _alerts.ShowAlert(uid, pain.Alert, 0);
    }

    private void OnAlertSeverityCheck(EntityUid uid, PainComponent pain, ref BeforeAlertSeverityCheckEvent args)
    {
        if (args.CurrentAlert == pain.Alert)
        {
            args.Severity = Math.Min((short)pain.CurrentPainLevel, _alerts.GetMaxSeverity(pain.Alert));
            args.CancelUpdate = true;
        }
    }

    private void OnDamageChanged(EntityUid uid, PainComponent comp, ref DamageChangedEvent args)
    {
        UpdateCurrentPain(uid, comp, args.Damageable.Damage);
    }

    public void TryChangePainLevelTo(EntityUid uid, int level, PainComponent? pain = null)
    {
        if (!Resolve(uid, ref pain))
            return;

        if (pain.NextPainLevelUpdateTime > _timing.CurTime)
            return;

        pain.NextPainLevelUpdateTime = _timing.CurTime + pain.PainLevelUpdateRate;

        // `CompareTo()` returns either 1, 0, or -1, so this modifies it one step at a time.
        pain.CurrentPainLevel += level.CompareTo(pain.CurrentPainLevel);

        DirtyField(uid, pain, nameof(PainComponent.CurrentPainLevel));
        DirtyField(uid, pain, nameof(PainComponent.NextPainLevelUpdateTime));

        if (pain.CurrentPainLevel <= _alerts.GetMaxSeverity(pain.Alert))
            _alerts.ShowAlert(uid, pain.Alert, (short)pain.CurrentPainLevel);
    }
    public void AddPainModifier(EntityUid uid, TimeSpan duration, FixedPoint2 effectStrength, PainModifierType type, PainComponent? pain = null)
    {
        var expireAt = _timing.CurTime + duration;
        var mod = new PainModifier(expireAt, effectStrength, type);
        AddPainModifier(uid, mod, pain);
    }

    public void AddPainModifier(EntityUid uid, PainModifier mod, PainComponent? pain = null)
    {
        if (!Resolve(uid, ref pain))
            return;

        pain.PainModifiers.Add(mod);
        UpdateCurrentPainPercentage(uid, pain);
        DirtyField(uid, pain, nameof(PainComponent.PainModifiers));
    }

    private void UpdateCurrentPainPercentage(EntityUid uid, PainComponent comp)
    {
        var maxPainReductionModificatorStrength = FixedPoint2.Zero;
        var painIncrease = FixedPoint2.Zero;
        var painIncreases = comp.PainModifiers.Where(mod => mod.Type == PainModifierType.PainIncrease);
        var painReductions = comp.PainModifiers.Where(mod => mod.Type == PainModifierType.PainReduction);
        // get max pain reduction, sum pain increase
        if (painIncreases.Any())
            painIncrease = painIncreases.Select(mod => mod.EffectStrength).Sum();
        if (painReductions.Any())
            maxPainReductionModificatorStrength = painReductions.Max(mod => mod.EffectStrength);

        var realCurrentPain = comp.CurrentPain + painIncrease;
        // Pain reduction effectiveness linear decreases as the pain goes up
        var newPainReduction = FixedPoint2.Max(0, -realCurrentPain * comp.PainReductionDecreaseRate + maxPainReductionModificatorStrength);
        comp.CurrentPainPercentage = FixedPoint2.Clamp(realCurrentPain - newPainReduction, 0, 100);
        DirtyField(uid, comp, nameof(PainComponent.CurrentPainPercentage));
    }

    private void UpdateCurrentPain(EntityUid uid, PainComponent comp, DamageSpecifier damage)
    {
        var newCurrentPain = FixedPoint2.Zero;

        newCurrentPain += GetDamageGroupPain(damage, BruteGroup, comp.BrutePainMultiplier);
        newCurrentPain += GetDamageGroupPain(damage, BurnGroup, comp.BurnPainMultiplier);
        newCurrentPain += GetDamageGroupPain(damage, ToxinGroup, comp.ToxinPainMultiplier);
        newCurrentPain += GetDamageGroupPain(damage, AirlossGroup, comp.AirlossPainMultiplier);

        if (comp.CurrentPain != newCurrentPain)
        {
            comp.CurrentPain = newCurrentPain;
            DirtyField(uid, comp, nameof(PainComponent.CurrentPain));
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

            UpdateCurrentPainPercentage(uid, pain);

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

            TryChangePainLevelTo(uid, expectedPainLevel, pain);

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
