using Robust.Shared.Serialization;

namespace Content.Shared._RMC14.Medical.Pain;

[Serializable, NetSerializable]
public sealed class PainLevelChangedEvent(NetEntity target, int oldLevel, int newLevel) : EntityEventArgs
{
    public NetEntity Target = target;
    public int OldLevel = oldLevel;
    public int NewLevel = newLevel;
}
