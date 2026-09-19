using Content.Client.UserInterface.Systems.DamageOverlays.Overlays;
using Content.IntegrationTests.Pair;
using Content.Shared._RMC14.Medical.Pain;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.StatusEffect;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
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
  parent: MobDamageable
  id: {TestPainEntityId}
  components:
  - type: MobState
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

    private static FixedPoint2 _damageAmount = 30;

    private TestPair _pair;
    private PainSystem _sPainSystem;
    private DamageableSystem _sDamageableSystem;
    private SharedMapSystem _sMapSystem;

    private EntityUid _sPainEntity;
    private PainComponent _sPainComp;
    private DamageableComponent _sDamageableComp;

    //[SetUp] // Called manually per-test to allow for parameters.
    private async Task SetUp(PoolSettings settings = null, string painEntId = TestPainEntityId)
    {
        _pair = await PoolManager.GetServerClient(settings);
        var server = _pair.Server;

        _sPainSystem = server.EntMan.System<PainSystem>();
        _sDamageableSystem = server.EntMan.System<DamageableSystem>();
        _sMapSystem = server.EntMan.System<SharedMapSystem>();

        await _pair.CreateTestMap();
        await server.WaitPost(() =>
        {
            _sPainEntity = server.EntMan.SpawnEntity(painEntId, _pair.TestMap.MapCoords);
            _sPainComp = server.EntMan.GetComponent<PainComponent>(_sPainEntity);
            _sDamageableComp = server.EntMan.GetComponent<DamageableComponent>(_sPainEntity);

            if (settings?.Connected is true)
                server.PlayerMan.SetAttachedEntity(server.PlayerMan.GetSessionById(_pair.Client.Session.UserId), _sPainEntity);
        });
        await _pair.ReallyBeIdle(5);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pair.Server.WaitPost(() => _sMapSystem.DeleteMap(_pair.TestMap.MapId));
        await _pair.CleanReturnAsync();
    }

    [TestCase("Brute")]
    [TestCase("Burn")]
    [TestCase("Toxin")]
    [TestCase("Airloss")]
    [Repeat(50)] // temp
    public async Task TestDamageGroups(string damageGroupProtoId)
    {
        await SetUp();
        // Everything should be clear initially.
        Assert.Multiple(() =>
        {
            Assert.That(_sDamageableComp.Damage.AnyPositive(), Is.False);
            Assert.That(_sPainComp.BasePain, Is.EqualTo(FixedPoint2.Zero));
        });

        await SetDamage(_damageAmount, damageGroupProtoId);

        var damageGroupPrototype = _pair.Server.ProtoMan.Index<DamageGroupPrototype>(damageGroupProtoId);
        _sDamageableComp.Damage.TryGetDamageInGroup(damageGroupPrototype, out var damage);

        // Make sure damage was actually applied, and that `PainComponent` updated correctly.
        Assert.Multiple(() =>
        {
            Assert.That(damage, Is.EqualTo(_damageAmount));
            Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount * _sPainComp.DamageGroupPainMultipliers[damageGroupProtoId]));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
        });
    }

    [TestCaseSource(nameof(GetPainModifierTypes))]
    [TestOf(typeof(PainModifier))]
    [Repeat(50)] // temp
    public async Task TestPainModifiers(PainModifierType modifierType)
    {
        await SetUp();
        const int modifierStrength = 20;

        // Add some damage.
        await SetDamage(_damageAmount);

        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
        });

        var modifier = new PainModifier(TimeSpan.FromHours(1), modifierStrength, modifierType);
        _sPainSystem.AddPainModifier(_sPainEntity, modifier);
        await _pair.Server.WaitRunTicks(5);

        // Make sure the modifier was applied correctly.
        var expectedPerceivedPain = FixedPoint2.Clamp(modifierType switch
        {
            PainModifierType.PainIncrease => _sPainComp.BasePain + modifierStrength,
            PainModifierType.PainReduction => _sPainComp.BasePain - FixedPoint2.Max(0, -_sPainComp.BasePain * _sPainComp.PainReductionDecreaseRate + modifierStrength),
            _ => throw new InvalidOperationException()
        }, 0, 100);
        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.PainModifiers.Single(), Is.EqualTo(modifier));
            Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(expectedPerceivedPain));
        });

        _sPainSystem.ClearPainModifiers(_sPainEntity);
        await _pair.Server.WaitRunTicks(5);

        // Make sure the modifier was removed correctly.
        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.PainModifiers, Is.Empty);
            Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
        });
    }

    [Test]
    [TestOf(typeof(PainLevel))]
    [Repeat(50)] // temp
    public async Task TestPainLevels()
    {
        await SetUp();
        // TODO RMC14: Change to new status effect system when more is ported.
        var _sStatusEffectSystem = _pair.Server.EntMan.System<StatusEffectsSystem>();

        var levelThresholds = _sPainComp.PainLevels
            .Select((l, idx) => (l.Threshold, idx))
            .Skip(1); // skip level 0

        // Ensure that reaching each threshold in `PainComponent.PainLevels` correctly sets all vars and applies the level's effects.
        foreach (var (threshold, painLevelIdx) in levelThresholds)
        {
            await SetDamage(threshold);
            Assert.Multiple(() =>
            {
                Assert.That(_sPainComp.BasePain, Is.EqualTo(threshold));
                Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
                Assert.That(_sPainComp.CurrentPainLevelIdx, Is.EqualTo(painLevelIdx));
                Assert.That(_sStatusEffectSystem.HasStatusEffect(_sPainEntity, $"PainLevel{painLevelIdx}"));
            });
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
        await SetUp();
        await _pair.Server.WaitAssertion(() =>
        {
            Assert.Throws(
                Is.TypeOf<EntityCreationException>()
                    .With.InnerException.TypeOf<DebugAssertException>()
                    .And.InnerException.Message.Contains("entries must be written in order of their thresholds"),
                () => _pair.Server.EntMan.SpawnEntity(UnorderedThresholdEntId, _pair.TestMap.MapCoords));
        });
    }

    [Test]
    [Repeat(50)] // temp
    public async Task TestClientPainOverlay()
    {
        await SetUp(new PoolSettings { Connected = true }, SlowUpdateEntityId);
        var listenerSystem = _pair.Server.System<PainLevelListenerSystem>();
        var cOverlayMan = _pair.Client.ResolveDependency<IOverlayManager>();
        var damageOverlay = cOverlayMan.GetOverlay<DamageOverlay>();

        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(damageOverlay.PainLevel, Is.EqualTo(0f));
        });

        // Below 5 damage shouldn't be visible in the overlay.
        await SetDamage(4);
        await _pair.SyncTicks();
        Assert.Multiple(() =>
        {
            Assert.That(_sPainComp.BasePain, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
            Assert.That(damageOverlay.PainLevel, Is.Zero);
        });

        // Anything over that *should* be visible.
        var highestPainLevel = _sPainComp.PainLevels.Last();
        await SetDamage(highestPainLevel.Threshold);
        await _pair.SyncTicks();

        // `PainComponent.CurrentPainLevelIdx` increases one step at a time every `PainComponent.PainLevelUpdateRate` seconds,
        // so make sure that the damage overlay updates correctly for each step up.
        var cancellationToken = new CancellationTokenSource();
        await TestEachPainLevelUntilTarget(highestPainLevel.Threshold, highestPainLevel);

        // And it should all go back to zero when the client's entity is healed.
        await SetDamage(0);
        await _pair.SyncTicks();
        // Same thing as above, checking each step down.
        await TestEachPainLevelUntilTarget(FixedPoint2.Zero, _sPainComp.PainLevels[0]);

        async Task TestEachPainLevelUntilTarget(FixedPoint2 expectedDamage, PainLevel targetPainLevel)
        {
            cancellationToken.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                // The overlay `PainLevel` value is either `PerceivedPain` clamped between the thresholds of the current and next pain levels.
                float expectedOverlayPainLevel;
                if (_sPainComp.PainLevels.TryGetValue(_sPainComp.CurrentPainLevelIdx, out var currentPainLevel) &&
                    _sPainComp.PainLevels.TryGetValue(_sPainComp.CurrentPainLevelIdx + 1, out var nextPainLevel))
                {
                    expectedOverlayPainLevel = (FixedPoint2.Clamp(_sPainComp.PerceivedPain, currentPainLevel.Threshold, nextPainLevel.Threshold)
                        / 100).Float(); // (as a float percentage)
                }
                // Or if there is no next pain level, just `PerceivedPain`.
                else
                {
                    expectedOverlayPainLevel = (_sPainComp.PerceivedPain / 100).Float(); // (as a float percentage)
                }

                // Make sure everything matches up with where it should be for this pain level.
                Assert.Multiple(() =>
                {
                    Assert.That(_sPainComp.BasePain, Is.EqualTo(expectedDamage));
                    Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
                    Assert.That(damageOverlay.PainLevel, Is.EqualTo(expectedOverlayPainLevel));
                });

                if (currentPainLevel == targetPainLevel)
                    break;

                await listenerSystem.WaitForPainLevelChange(_pair, cancellationToken.Token);
            }
        }
    }

    private async Task SetDamage(FixedPoint2 amount, ProtoId<DamageGroupPrototype>? damageGroupOverride = null)
    {
        var damageGroupPrototype = _pair.Server.ProtoMan.Index(damageGroupOverride ?? "Brute");
        var specifier = new DamageSpecifier(damageGroupPrototype, amount);
        _sDamageableSystem.SetDamage(_sPainEntity, _sDamageableComp, specifier);
        await _pair.Server.WaitRunTicks(5);
    }

    private static PainModifierType[] GetPainModifierTypes()
    {
        return Enum.GetValues<PainModifierType>();
    }

    /*
    Todo (potentially):

    [X] Damage groups => `BasePain` test with correct modifiers
    [X] BasePain == PerceivedPain after update cycle
    [X] Pain increase modifier test
    [X] Pain reduction modifier falloff test
    [X] Ensure assert fails if painlevels aren't in order
    [X] Thresholds and threshold effects work and apply properly
    [ ] Vars clear and resets correctly on death + revive. Test dying with pain and with no pain
    [ ] Test painknockoutcomponent?
    [X] Client pain overlay test (increases on damage up to pain level threshold, removed on heal)
    */
}

[RegisterComponent]
public sealed partial class PainLevelDummyComponent : Component;

public sealed partial class PainLevelListenerSystem : EntitySystem
{
    private bool _eventTriggered;

    public override void Initialize()
    {
        SubscribeLocalEvent<PainLevelChangedEvent>(OnPainLevelChanged);
    }

    private void OnPainLevelChanged(ref PainLevelChangedEvent ev)
    {
        _eventTriggered = true;
    }

    public async Task WaitForPainLevelChange(TestPair pair, CancellationToken ct)
    {
        _eventTriggered = false;
        while (!_eventTriggered)
        {
            Assert.DoesNotThrow(ct.ThrowIfCancellationRequested, $"{nameof(WaitForPainLevelChange)} timed out waiting for {nameof(PainLevelChangedEvent)}!");
            await pair.RunTicksSync(pair.SecondsToTicks(0.1f));
        }
    }
}
