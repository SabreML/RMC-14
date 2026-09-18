using Content.IntegrationTests.Pair;
using Content.Shared._RMC14.Medical.Pain;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.StatusEffect;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.IntegrationTests._RMC14;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[TestOf(typeof(PainComponent)), TestOf(typeof(PainSystem))]
public sealed class PainTests
{
    private const string TestPainEntityId = "TestPainEntity";
    private const string UnorderedThresholdEntId = "UnorderedThresholdEntity";

    [TestPrototypes]
    private const string Prototypes = $"""
- type: entity
  parent: MobDamageable
  id: {TestPainEntityId}
  components:
  - type: StatusEffects
    allowed:
    - PainLevel1
    - PainLevel2
    - PainLevel3
    - PainLevel4
    - PainLevel5
    - PainLevel6
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
    - threshold: 75
      levelEffects:
      - !type:GenericStatusEffect
        key: PainLevel5
        component: PainLevelDummy
    - threshold: 85
      levelEffects:
      - !type:GenericStatusEffect
        key: PainLevel6
        component: PainLevelDummy

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
- type: statusEffect
  id: PainLevel5
- type: statusEffect
  id: PainLevel6
""";

    private static FixedPoint2 _damageAmount = 30;

    private TestPair _pair;
    private IEntityManager _sEntMan;
    private IPrototypeManager _sProtoMan;
    private PainSystem _sPainSystem;
    private DamageableSystem _sDamageableSystem;
    private StatusEffectsSystem _sStatusEffectSystem; // todo: look into changing `GenericStatusEffect` to `ModifyStatusEffect`?
    private SharedMapSystem _sMapSystem;

    private EntityUid _sPainEntity;
    private PainComponent _sPainComp;
    private DamageableComponent _sDamageableComp;

    [SetUp]
    public async Task Setup()
    {
        _pair = await PoolManager.GetServerClient();
        var server = _pair.Server;

        _sEntMan = server.ResolveDependency<IEntityManager>();
        _sProtoMan = server.ResolveDependency<IPrototypeManager>();
        _sPainSystem = _sEntMan.System<PainSystem>();
        _sDamageableSystem = _sEntMan.System<DamageableSystem>();
        _sStatusEffectSystem = _sEntMan.System<StatusEffectsSystem>();
        _sMapSystem = _sEntMan.System<SharedMapSystem>();

        await _pair.CreateTestMap();
        await server.WaitPost(() =>
        {
            _sPainEntity = _sEntMan.SpawnEntity(TestPainEntityId, _pair.TestMap.MapCoords);
            _sPainComp = _sEntMan.GetComponent<PainComponent>(_sPainEntity);
            _sDamageableComp = _sEntMan.GetComponent<DamageableComponent>(_sPainEntity);
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
        var damageGroupPrototype = _sProtoMan.Index<DamageGroupPrototype>(damageGroupProtoId);
        var specifier = new DamageSpecifier(damageGroupPrototype, _damageAmount);

        // Everything should be clear initially.
        Assert.Multiple(() =>
        {
            Assert.That(_sDamageableComp.Damage.AnyPositive(), Is.False);
            Assert.That(_sPainComp.BasePain, Is.EqualTo(FixedPoint2.Zero));
        });

        _sDamageableSystem.SetDamage(_sPainEntity, _sDamageableComp, specifier);
        await _pair.Server.WaitRunTicks(5);
        _sDamageableComp.Damage.TryGetDamageInGroup(damageGroupPrototype, out var damage);

        // Make sure damage was actually applied, and that `PainComponent` updated correctly.
        Assert.Multiple(() =>
        {
            Assert.That(damage, Is.EqualTo(_damageAmount));
            Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount * _sPainComp.DamageGroupPainMultipliers[damageGroupProtoId]));
            Assert.That(_sPainComp.PerceivedPain, Is.EqualTo(_sPainComp.BasePain));
        });
    }

    [TestCase(PainModifierType.PainReduction)]
    [TestCase(PainModifierType.PainIncrease)]
    [TestOf(typeof(PainModifier))]
    [Repeat(50)] // temp
    public async Task TestPainModifiers(PainModifierType modifierType)
    {
        const int modifierStrength = 20;

        // Add some damage.
        var damageGroupPrototype = _sProtoMan.Index<DamageGroupPrototype>("Brute");
        var specifier = new DamageSpecifier(damageGroupPrototype, _damageAmount);
        _sDamageableSystem.SetDamage(_sPainEntity, _sDamageableComp, specifier);
        await _pair.Server.WaitRunTicks(5);

        Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount).And.EqualTo(_sPainComp.PerceivedPain));

        var modifier = new PainModifier(TimeSpan.FromHours(1), modifierStrength, modifierType);
        _sPainSystem.AddPainModifier(_sPainEntity, modifier);
        await _pair.Server.WaitRunTicks(5);

        // Make sure the modifier was applied correctly.
        var expectedPerceivedPain = FixedPoint2.Clamp(modifierType switch
        {
            PainModifierType.PainIncrease => _sPainComp.BasePain + modifierStrength,
            PainModifierType.PainReduction => _sPainComp.BasePain - FixedPoint2.Max(0, -_sPainComp.BasePain * _sPainComp.PainReductionDecreaseRate + modifierStrength),
            _ => throw new InvalidOperationException() // shouldn't be possible in a test environment but just to appease it
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
            Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount).And.EqualTo(_sPainComp.PerceivedPain));
        });
    }

    [Test]
    [TestOf(typeof(PainLevel))]
    [Repeat(50)] // temp
    public async Task TestPainLevels()
    {
        var levelThresholds = _sPainComp.PainLevels
            .Select((l, idx) => (idx, l.Threshold))
            .Skip(1); // skip level 0

        // Ensure that reaching each threshold in `PainComponent.PainLevels` correctly sets all vars and applies the level's effects.
        foreach (var (painLevelIdx, threshold) in levelThresholds)
        {
            var specifier = new DamageSpecifier(_sProtoMan.Index<DamageGroupPrototype>("Brute"), threshold);
            _sDamageableSystem.SetDamage(_sPainEntity, _sDamageableComp, specifier);
            await _pair.Server.WaitRunTicks(5);

            Assert.Multiple(() =>
            {
                Assert.That(_sPainComp.BasePain, Is.EqualTo(threshold).And.EqualTo(_sPainComp.PerceivedPain));
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
        await _pair.Server.WaitAssertion(() =>
        {
            Assert.Throws(
                Is.TypeOf<EntityCreationException>()
                    .With.InnerException.TypeOf<DebugAssertException>()
                    .And.InnerException.Message.Contains("entries must be written in order of their thresholds"),
                () => _sEntMan.SpawnEntity(UnorderedThresholdEntId, _pair.TestMap.MapCoords));
        });
    }

    /*
    Todo (potentially):

    [X] Damage groups => `BasePain` test with correct modifiers
    [X] BasePain == PerceivedPain after update cycle
    [X] Pain increase modifier test
    [X] Pain reduction modifier falloff test
    [X] Ensure assert fails if painlevels aren't in order
    [X] Thresholds and threshold effects work and apply properly
    [ ] Painkiller/decreasepain reagents test
    [ ] Vars clear and resets correctly on death + revive
    [ ] Test painknockoutcomponent?
    [ ] Client pain overlay test (increases on damage up to pain level threshold, removed on heal)
    */
}

[RegisterComponent]
public sealed partial class PainLevelDummyComponent : Component;
