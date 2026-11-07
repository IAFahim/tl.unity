using Unity.Entities;

namespace Tl
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class TlSimulationGroup : ComponentSystemGroup
    {
    }
}
