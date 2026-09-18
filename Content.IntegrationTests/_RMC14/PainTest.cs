using Content.IntegrationTests.Pair;
using Content.Shared._RMC14.Medical.Pain;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.IntegrationTests._RMC14;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[TestOf(typeof(PainComponent)), TestOf(typeof(PainSystem))]
public sealed class PainTest
{
    private const string TestPainEntityId = "TestPainEntityId";

    [TestPrototypes]
    private const string Prototype = $"""
- type: entity
  parent: MobDamageable
  id: {TestPainEntityId}
  name: {TestPainEntityId}
  components:
  - type: Pain
    updateRate: 0
    painLevelUpdateRate: 0
    painLevels:
    - threshold: 0
      levelEffects: []
""";

    private static FixedPoint2 _damageAmount = 30;

    private TestPair _pair;
    private IEntityManager _sEntMan;
    private IPrototypeManager _sProtoMan;
    private PainSystem _sPainSystem;
    private DamageableSystem _sDamageableSystem;
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

        _sDamageableSystem.TryChangeDamage(_sPainEntity, specifier, true);
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
    public async Task TestPainModifiers(PainModifierType modifierType)
    {
        const int modifierStrength = 20;

        // Add some damage.
        var damageGroupPrototype = _sProtoMan.Index<DamageGroupPrototype>("Brute");
        var specifier = new DamageSpecifier(damageGroupPrototype, _damageAmount);
        _sDamageableSystem.TryChangeDamage(_sPainEntity, specifier, true);
        await _pair.Server.WaitRunTicks(5);

        Assert.That(_sPainComp.BasePain, Is.EqualTo(_damageAmount).And.EqualTo(_sPainComp.PerceivedPain));

        var modifier = new PainModifier(TimeSpan.FromSeconds(5), modifierStrength, modifierType);
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

    /*
    Todo (potentially):

    [X] Damage groups => `BasePain` test with correct modifiers
    [X] BasePain == PerceivedPain after update cycle
    [X] Pain increase modifier test
    [X] Pain reduction modifier falloff test
    [ ] Ensure assert fails if painlevels aren't in order
    [ ] Thresholds and threshold effects work and apply properly
    [ ] Painkiller/decreasepain reagents test
    [ ] Clears and resets correctly on death + revive
    [ ] Test painknockoutcomponent?
    [ ] Client pain overlay test (increases on damage up to pain level threshold, removed on heal)
    */
}
