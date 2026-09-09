using Robust.Shared.GameStates;
using Content.Shared.FixedPoint;
using Content.Shared.EntityEffects;
using Content.Shared.Alert;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._RMC14.Medical.Pain;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), AutoGenerateComponentPause]
public sealed partial class PainComponent : Component
{
    /// <summary>
    /// Base pain value derived from overall damage to the body, without accounting for any <see cref="PainModifiers"/>.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public FixedPoint2 CurrentPain = FixedPoint2.Zero;

    /// <summary>
    /// 0 to 100 value representing how much pain the player actually feels after applying any <see cref="PainModifiers"/> like painkillers.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public FixedPoint2 CurrentPainPercentage = FixedPoint2.Zero;

    /// <summary>
    /// Current index in the <see cref="PainLevels"/> list.
    /// This is set based on the highest <see cref="PainLevel.Threshold"/> passed by <see cref="CurrentPainPercentage"/>.
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public int CurrentPainLevel = 0;

    /// <summary>
    /// Time between each update of this component in <see cref="PainSystem.Update(float)"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan UpdateRate = TimeSpan.FromSeconds(1);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    [AutoPausedField]
    public TimeSpan NextUpdateTime = new(0);

    /// <summary>
    /// Time between each update of <see cref="CurrentPainLevel"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan PainLevelUpdateRate = TimeSpan.FromSeconds(2);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    [AutoPausedField]
    public TimeSpan NextPainLevelUpdateTime = new(0);

    [ViewVariables, Access(typeof(PainSystem)), AutoNetworkedField]
    public List<PainModifier> PainModifiers = [];

    [DataField, AutoNetworkedField]
    public FixedPoint2 PainReductionDecreaseRate = FixedPoint2.New(0.25);

    [DataField, AutoNetworkedField]
    public FixedPoint2 BrutePainMultiplier = FixedPoint2.New(1);

    [DataField, AutoNetworkedField]
    public FixedPoint2 BurnPainMultiplier = FixedPoint2.New(1.2);

    [DataField, AutoNetworkedField]
    public FixedPoint2 ToxinPainMultiplier = FixedPoint2.New(1.5);

    [DataField, AutoNetworkedField]
    public FixedPoint2 AirlossPainMultiplier = FixedPoint2.Zero;

    [DataField(required: true, serverOnly: true)]
    public List<PainLevel> PainLevels = [];

    [DataField, AutoNetworkedField]
    public ProtoId<AlertPrototype> Alert = "HumanoidPainHealth";
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class PainModifier
{
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan ExpireAt;
    public FixedPoint2 EffectStrength;
    public PainModifierType Type;

    public PainModifier(TimeSpan expireAt, FixedPoint2 strength, PainModifierType type)
    {
        ExpireAt = expireAt;
        EffectStrength = strength;
        Type = type;
    }
}

[DataRecord]
public record struct PainLevel(FixedPoint2 Threshold, List<EntityEffect> LevelEffects);

public enum PainModifierType : byte
{
    PainReduction,
    PainIncrease,
}
