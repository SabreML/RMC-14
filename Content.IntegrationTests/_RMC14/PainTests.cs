#nullable enable
using Content.Client.UserInterface.Systems.DamageOverlays.Overlays;
using Content.IntegrationTests.Pair;
using Content.Shared._RMC14.Medical.Pain;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.StatusEffect;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Content.IntegrationTests._RMC14;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[TestOf(typeof(PainComponent)), TestOf(typeof(PainSystem))]
public sealed class PainTests
{
    private const string TestPainEntityId = "TestPainEntity";
    private const string SlowUpdateEntityId = "SlowUpdateEntity";
    private const string UnorderedThresholdEntId = "UnorderedThresholdEntity";

    [TestPrototypes]
    private const string Prototypes = $"""
- type: entity
  id: {TestPainEntityId}
  components:
  - type: Damageable
    damageContainer: Biological
  - type: MobState
  - type: MobThresholds
    thresholds:
      0: Alive
      100: Critical
      200: Dead
  - type: StatusEffects
    allowed:
    - PainLevel1
    - PainLevel2
    - PainLevel3
    - PainLevel4
  - type: Pain
    updateRate: 0
    painLevelUpdateRate: 0
    painLevels:
    - threshold: 0
      levelEffects: []
    - threshold: 20
      levelEffects:
      - !type:GenericStatusEffect
        key: PainLevel1
        component: PainLevelDummy
    - threshold: 30
      levelEffects:
      - !type:GenericStatusEffect
        key: PainLevel2
        component: PainLevelDummy
    - threshold: 40
      levelEffects:
      - !type:GenericStatusEffect
        key: PainLevel3
        component: PainLevelDummy
    - threshold: 60
      levelEffects:
      - !type:GenericStatusEffect
        key: PainLevel4
        component: PainLevelDummy

- type: entity
  parent: {TestPainEntityId}
  id: {SlowUpdateEntityId}
  components:
  - type: Pain
    painLevelUpdateRate: 2

- type: entity
  parent: {TestPainEntityId}
  id: {UnorderedThresholdEntId}
  components:
  - type: Pain
    painLevels:
    - threshold: 0
      levelEffects: []
    - threshold: 50
      levelEffects: []
    - threshold: 25
      levelEffects: []

- type: statusEffect
  id: PainLevel1
- type: statusEffect
  id: PainLevel2
- type: statusEffect
  id: PainLevel3
- type: statusEffect
  id: PainLevel4
""";

    // Enough to cause pain, but not quite enough to pass a `PainLevel` threshold.
    private const int SmallDamageAmount = 15;
    // Medium amount of pain, now up to the third `PainLevel` threshold.
    private const int BigDamageAmount = 30;

    private TestPair _pair = default!;
    private PainSystem _sPainSystem = default!;
    private DamageableSystem _sDamageableSystem = default!;
    private MobStateSystem _sMobStateSystem = default!;

    private EntityUid _sPainEntity;
    private PainComponent _sPainComp = default!;
    private DamageableComponent _sDamageableComp = default!;
    private DamageOverlay _cDamageOverlay = default!;

    private static PainModifierType[] GetPainModifierTypes()
    {
        return Enum.GetValues<PainModifierType>();
    }

    //[SetUp] // Called manually per-test to allow for parameters.
    private async Task SetUp(string painEntId = TestPainEntityId)
    {
        _pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = _pair.Server;

        _sPainSystem = server.EntMan.System<PainSystem>();
        _sDamageableSystem = server.EntMan.System<DamageableSystem>();
        _sMobStateSystem = server.EntMan.System<MobStateSystem>();

        await server.WaitPost(() =>
        {
            _sPainEntity = server.EntMan.SpawnEntity(painEntId, MapCoordinates.Nullspace);
            _sPainComp = server.EntMan.GetComponent<PainComponent>(_sPainEntity);
            _sDamageableComp = server.EntMan.GetComponent<DamageableComponent>(_sPainEntity);

            server.PlayerMan.SetAttachedEntity(server.PlayerMan.GetSessionById(_pair.Client.Session!.UserId), _sPainEntity);
        });

        await _pair.RunTicksSync(5);
        var cOverlayMan = _pair.Client.ResolveDependency<IOverlayManager>();
        _cDamageOverlay = cOverlayMan.GetOverlay<DamageOverlay>();

        await _pair.ReallyBeIdle(5);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pair.CleanReturnAsync();
    }

    [Test]
    [Repeat(50)] // temp
    public async Task TestDamageGroups(
        [Values(SmallDamageAmount, BigDamageAmount)] int damageAmount,
        [Values("Brute", "Burn", "Toxin", "Airloss")] string damageGroupProtoId)
    {
        await SetUp();
        // Everything should be clear initially.
        Assert.Multiple(() =>
        {
            Assert.That(_sDamageableComp.Damage.AnyPositive(), Is.False);
            AssertPainVarsMatchExpected(FixedPoint2.Zero);
        });

        await SetDamage(damageAmount, damageGroupProtoId);
        var expectedBasePain = damageAmount * _sPainComp.DamageGroupPainMultipliers[damageGroupProtoId];
        await WaitUntilPainLevelReachesTarget(expectedBasePain);

        var damageGroupPrototype = _pair.Server.ProtoMan.Index<DamageGroupPrototype>(damageGroupProtoId);
        _sDamageableComp.Damage.TryGetDamageInGroup(damageGroupPrototype, out var damage);

        // Make sure damage was actually applied, and that `PainComponent` updated correctly.
        Assert.Multiple(() =>
        {
            Assert.That(damage, Is.EqualTo(FixedPoint2.New(damageAmount)));
            AssertPainVarsMatchExpected(expectedBasePain);
        });
    }

    [Test]
    [TestOf(typeof(PainModifier))]
    [Repeat(50)] // temp
    public async Task TestPainModifiers(
        [Values(0, SmallDamageAmount, BigDamageAmount)] int initialDamage,
        [ValueSource(nameof(GetPainModifierTypes))] PainModifierType modifierType)
    {
        await SetUp();
        const int modifierStrength = 20;

        // Add some damage.
        if (initialDamage != 0)
        {
            await SetDamage(initialDamage);
            await WaitUntilPainLevelReachesTarget(initialDamage);
        }
        AssertPainVarsMatchExpected(initialDamage);

        // Add the modifier.
        var modifier = new PainModifier(TimeSpan.FromHours(1), modifierStrength, modifierType);
        _sPainSystem.AddPainModifier(_sPainEntity, modifier);
        await _pair.RunTicksSync(5);

        // Make sure the modifier was applied correctly.
        var expectedPerceivedPain = FixedPoint2.Clamp(modifierType switch
        {
            PainModifierType.PainIncrease => initialDamage + modifierStrength,
            PainModifierType.PainReduction => initialDamage - FixedPoint2.Max(0, -initialDamage * _sPainComp.PainReductionDecreaseRate + modifierStrength),
            _ => throw new InvalidOperationException()
        }, 0, 100);

        await WaitUntilPainLevelReachesTarget(initialDamage, expectedPerceivedPain);
        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.PainModifiers.Single(), Is.EqualTo(modifier));
            AssertPainVarsMatchExpected(initialDamage, expectedPerceivedPain);
        });

        _sPainSystem.ClearPainModifiers(_sPainEntity);
        await _pair.RunTicksSync(5);
        await WaitUntilPainLevelReachesTarget(initialDamage);

        // Make sure the modifier was removed correctly.
        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.PainModifiers, Is.Empty);
            AssertPainVarsMatchExpected(initialDamage);
        });
    }

    [Test]
    [TestOf(typeof(PainLevel))]
    [Repeat(50)] // temp
    public async Task TestPainLevels()
    {
        await SetUp();
        // TODO RMC14: Change to new status effect system when more is ported.
        var statusEffectSystem = _pair.Server.EntMan.System<StatusEffectsSystem>();

        var indexedPainLevels = _sPainComp.PainLevels
            .Select((level, idx) => (level, idx));

        // Test everything going from zero to max.
        await TestPainLevel(indexedPainLevels.Skip(1)); // Skip the first level since we're there already.

        // And the same in the other direction.
        await TestPainLevel(indexedPainLevels.Reverse().Skip(1)); // Skip the last level since we're there already.

        async Task TestPainLevel(IEnumerable<(PainLevel, int)> painLevels)
        {
            foreach (var (level, idx) in painLevels)
            {
                await SetDamage(level.Threshold);

                // Make sure that reaching each threshold correctly sets all vars (checked in `WaitUntilPainLevelReachesTarget()`),
                // and applies the level's `EntityEffect`s.
                await WaitUntilPainLevelReachesTarget(level.Threshold, targetPainLevelIdx: idx);
                if (level.LevelEffects.Count != 0)
                    Assert.That(statusEffectSystem.HasStatusEffect(_sPainEntity, $"PainLevel{idx}"));
            }
        }
    }

#if !DEBUG
    [Ignore("Test checks for a `DebugAssertException`, which are only thrown in a debug build.")]
#endif
    [Test]
    [TestOf(typeof(PainLevel))]
    [Repeat(50)] // temp
    public async Task UnorderedPainLevelsThrowsException()
    {
        _pair = await PoolManager.GetServerClient();
        await _pair.Server.WaitAssertion(() =>
        {
            Assert.Throws(
                Is.TypeOf<EntityCreationException>()
                    .With.InnerException.TypeOf<DebugAssertException>()
                    .And.InnerException.Message.Contains("entries must be written in order of their thresholds"),
                () => _pair.Server.EntMan.SpawnEntity(UnorderedThresholdEntId, MapCoordinates.Nullspace));
        });
    }

    [Test]
    [Repeat(50)] // temp
    public async Task TestClientPainOverlay()
    {
        await SetUp(SlowUpdateEntityId);

        // Below 5 damage shouldn't be visible in the overlay.
        await SetDamage(4);
        await _pair.SyncTicks();
        AssertPainVarsMatchExpected(4);

        // Anything over that *should* be visible.
        var highestPainLevel = _sPainComp.PainLevels.Last();
        await SetDamage(highestPainLevel.Threshold);

        // `PainComponent.CurrentPainLevelIdx` increases one step at a time every `PainComponent.PainLevelUpdateRate` seconds,
        // so wait for it to catch up, running the standard assert checks after each level change.
        await WaitUntilPainLevelReachesTarget(highestPainLevel.Threshold, targetPainLevelIdx: _sPainComp.PainLevels.Count - 1);

        // And it should all go back to zero when the client's entity is healed.
        await SetDamage(0);
        // Same thing as above, checking each step down.
        await WaitUntilPainLevelReachesTarget(0);
    }

    [Test]
    [Repeat(50)] // temp
    public async Task TestDeathAndRevive(
        [Values(0, SmallDamageAmount, BigDamageAmount)] int initialDamage,
        [Values(0, SmallDamageAmount, BigDamageAmount, -SmallDamageAmount, -BigDamageAmount)] int afterDeathDamageChange)
    {
        await SetUp();

        if (initialDamage != 0)
        {
            await SetDamage(initialDamage);
            await WaitUntilPainLevelReachesTarget(initialDamage);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_sMobStateSystem.IsAlive(_sPainEntity), Is.True);
            AssertPainVarsMatchExpected(initialDamage);
        });

        await _pair.Server.WaitPost(() => _sMobStateSystem.ChangeMobState(_sPainEntity, MobState.Dead));
        await _pair.RunTicksSync(5);

        // On death, `PainSystem` should have cleared out all pain vars other than `BasePain`.
        Assert.Multiple(() =>
        {
            Assert.That(_sMobStateSystem.IsDead(_sPainEntity), Is.True);
            AssertPainVarsMatchExpected(initialDamage, 0);
        });

        var afterDeathDamage = FixedPoint2.Max(0, initialDamage + afterDeathDamageChange);
        // If damage changes at all while they're dead, only `BasePain` should be updated.
        if (afterDeathDamageChange != 0)
        {
            await SetDamage(afterDeathDamage);
            await WaitUntilPainLevelReachesTarget(afterDeathDamage, 0, 0);
            AssertPainVarsMatchExpected(afterDeathDamage, 0);
        }

        await _pair.Server.WaitPost(() => _sMobStateSystem.ChangeMobState(_sPainEntity, MobState.Alive));
        await _pair.RunTicksSync(5);
        await WaitUntilPainLevelReachesTarget(afterDeathDamage);

        // On revival, everything should go back to normal.
        Assert.Multiple(() =>
        {
            Assert.That(_sMobStateSystem.IsAlive(_sPainEntity), Is.True);
            AssertPainVarsMatchExpected(afterDeathDamage);
        });
    }

    private async Task WaitUntilPainLevelReachesTarget(
        FixedPoint2 expectedBasePain,
        FixedPoint2? expectedPerceivedPain = null,
        int? targetPainLevelIdx = null)
    {
        expectedPerceivedPain ??= expectedBasePain;
        targetPainLevelIdx ??= GetHighestPainLevelForDamageValue(expectedPerceivedPain.Value).Idx;

        if (_sPainComp.CurrentPainLevelIdx == targetPainLevelIdx)
            return; // misson accomplished?

        // Five second timeout limit just in case something goes wrong and it never actually changes.
        var cancellationToken = new CancellationTokenSource();
        cancellationToken.CancelAfter(TimeSpan.FromSeconds(500));

        while (_sPainComp.CurrentPainLevelIdx != targetPainLevelIdx)
        {
            // Make sure that everything remains correct after each level change.
            AssertPainVarsMatchExpected(expectedBasePain, expectedPerceivedPain, _sPainComp.CurrentPainLevelIdx);
            await WaitForPainLevelChange(cancellationToken.Token);
        }
    }

    private void AssertPainVarsMatchExpected(
        FixedPoint2 expectedBasePain,
        FixedPoint2? expectedPerceivedPain = null,
        int? expectedPainLevelIdx = null)
    {
        // non-nullable versions of the above parameters to avoid needing `.Value` everywhere
        var expectedPerceivedPainNotNull = expectedPerceivedPain ?? expectedBasePain;
        var expectedPainLevelIdxNotNull = expectedPainLevelIdx ?? GetHighestPainLevelForDamageValue(expectedPerceivedPainNotNull).Idx;

        // The size/strength of the clientside red vignette pain overlay thingy is defined as `PerceivedPain` clamped between
        // the thresholds of the current and next pain level (or no max value if `CurrentPainLevelIdx` is the highest it gets).
        var minOverlayPain = expectedPainLevelIdxNotNull == 0
            ? FixedPoint2.Zero // First in the list, so clamp to (0, currentLevel.Threshold)
            : _sPainComp.PainLevels[expectedPainLevelIdxNotNull].Threshold;
        var maxOverlayPain = expectedPainLevelIdxNotNull == _sPainComp.PainLevels.Count - 1
            ? FixedPoint2.MaxValue // Last in the list, so clamp to (currentLevel.Threshold, MaxValue)
            : _sPainComp.PainLevels[expectedPainLevelIdxNotNull + 1].Threshold;

        // That's then converted to a float percentage for use in the overlay.
        var expectedOverlayPainLevel = (FixedPoint2.Clamp(expectedPerceivedPainNotNull, minOverlayPain, maxOverlayPain) / 100).Float();
        // Values below 5% are considered "close enough" to max and discarded.
        if (expectedOverlayPainLevel < 0.05f)
            expectedOverlayPainLevel = 0f;

        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.BasePain, Is.EqualTo(expectedBasePain));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(expectedPerceivedPainNotNull));
            Assert.That(_sPainComp.CurrentPainLevelIdx, Is.EqualTo(expectedPainLevelIdxNotNull));
            Assert.That(_cDamageOverlay.PainLevel, Is.EqualTo(expectedOverlayPainLevel));
        });
    }

    private async Task WaitForPainLevelChange(CancellationToken ct)
    {
        var initialPainLevel = _sPainComp.CurrentPainLevelIdx;
        while (_sPainComp.CurrentPainLevelIdx == initialPainLevel)
        {
            Assert.DoesNotThrow(ct.ThrowIfCancellationRequested, $"{nameof(WaitForPainLevelChange)} timed out!");
            await _pair.RunTicksSync(_pair.SecondsToTicks(0.1f));
        }
    }

    private async Task SetDamage(FixedPoint2 amount, ProtoId<DamageGroupPrototype>? damageGroupOverride = null)
    {
        var damageGroupPrototype = _pair.Server.ProtoMan.Index(damageGroupOverride ?? "Brute");
        var specifier = new DamageSpecifier(damageGroupPrototype, amount);
        _sDamageableSystem.SetDamage(_sPainEntity, _sDamageableComp, specifier);
        await _pair.RunTicksSync(5);
    }

    private (PainLevel Level, int Idx) GetHighestPainLevelForDamageValue(FixedPoint2 damageValue)
    {
        return _sPainComp.PainLevels
            .Select((level, idx) => (level, idx))
            .Last(i => i.level.Threshold <= damageValue);
    }

    /*
    Todo (potentially):

    [X] Damage groups => `BasePain` test with correct modifiers
    [X] BasePain == PerceivedPain after update cycle
    [X] Pain increase modifier test
    [X] Pain reduction modifier falloff test
    [X] Ensure assert fails if painlevels aren't in order
    [X] Thresholds and threshold effects work and apply properly
    [X] Vars clear and resets correctly on death + revive. Test dying with pain and with no pain
    [ ] Test painknockoutcomponent?
    [X] Client pain overlay test (increases on damage up to pain level threshold, removed on heal)
    */
}

[RegisterComponent]
public sealed partial class PainLevelDummyComponent : Component;
