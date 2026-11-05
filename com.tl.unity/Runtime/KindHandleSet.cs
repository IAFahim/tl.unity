using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Tl.Unity
{
    public unsafe struct KindHandleSet
    {
        public const int Capacity = 64;

        public DynamicComponentTypeHandle E0;
        public DynamicComponentTypeHandle E1;
        public DynamicComponentTypeHandle E2;
        public DynamicComponentTypeHandle E3;
        public DynamicComponentTypeHandle E4;
        public DynamicComponentTypeHandle E5;
        public DynamicComponentTypeHandle E6;
        public DynamicComponentTypeHandle E7;
        public DynamicComponentTypeHandle E8;
        public DynamicComponentTypeHandle E9;
        public DynamicComponentTypeHandle E10;
        public DynamicComponentTypeHandle E11;
        public DynamicComponentTypeHandle E12;
        public DynamicComponentTypeHandle E13;
        public DynamicComponentTypeHandle E14;
        public DynamicComponentTypeHandle E15;
        public DynamicComponentTypeHandle E16;
        public DynamicComponentTypeHandle E17;
        public DynamicComponentTypeHandle E18;
        public DynamicComponentTypeHandle E19;
        public DynamicComponentTypeHandle E20;
        public DynamicComponentTypeHandle E21;
        public DynamicComponentTypeHandle E22;
        public DynamicComponentTypeHandle E23;
        public DynamicComponentTypeHandle E24;
        public DynamicComponentTypeHandle E25;
        public DynamicComponentTypeHandle E26;
        public DynamicComponentTypeHandle E27;
        public DynamicComponentTypeHandle E28;
        public DynamicComponentTypeHandle E29;
        public DynamicComponentTypeHandle E30;
        public DynamicComponentTypeHandle E31;
        public DynamicComponentTypeHandle E32;
        public DynamicComponentTypeHandle E33;
        public DynamicComponentTypeHandle E34;
        public DynamicComponentTypeHandle E35;
        public DynamicComponentTypeHandle E36;
        public DynamicComponentTypeHandle E37;
        public DynamicComponentTypeHandle E38;
        public DynamicComponentTypeHandle E39;
        public DynamicComponentTypeHandle E40;
        public DynamicComponentTypeHandle E41;
        public DynamicComponentTypeHandle E42;
        public DynamicComponentTypeHandle E43;
        public DynamicComponentTypeHandle E44;
        public DynamicComponentTypeHandle E45;
        public DynamicComponentTypeHandle E46;
        public DynamicComponentTypeHandle E47;
        public DynamicComponentTypeHandle E48;
        public DynamicComponentTypeHandle E49;
        public DynamicComponentTypeHandle E50;
        public DynamicComponentTypeHandle E51;
        public DynamicComponentTypeHandle E52;
        public DynamicComponentTypeHandle E53;
        public DynamicComponentTypeHandle E54;
        public DynamicComponentTypeHandle E55;
        public DynamicComponentTypeHandle E56;
        public DynamicComponentTypeHandle E57;
        public DynamicComponentTypeHandle E58;
        public DynamicComponentTypeHandle E59;
        public DynamicComponentTypeHandle E60;
        public DynamicComponentTypeHandle E61;
        public DynamicComponentTypeHandle E62;
        public DynamicComponentTypeHandle E63;
        public DynamicComponentTypeHandle A0;
        public DynamicComponentTypeHandle A1;
        public DynamicComponentTypeHandle A2;
        public DynamicComponentTypeHandle A3;
        public DynamicComponentTypeHandle A4;
        public DynamicComponentTypeHandle A5;
        public DynamicComponentTypeHandle A6;
        public DynamicComponentTypeHandle A7;
        public DynamicComponentTypeHandle A8;
        public DynamicComponentTypeHandle A9;
        public DynamicComponentTypeHandle A10;
        public DynamicComponentTypeHandle A11;
        public DynamicComponentTypeHandle A12;
        public DynamicComponentTypeHandle A13;
        public DynamicComponentTypeHandle A14;
        public DynamicComponentTypeHandle A15;
        public DynamicComponentTypeHandle A16;
        public DynamicComponentTypeHandle A17;
        public DynamicComponentTypeHandle A18;
        public DynamicComponentTypeHandle A19;
        public DynamicComponentTypeHandle A20;
        public DynamicComponentTypeHandle A21;
        public DynamicComponentTypeHandle A22;
        public DynamicComponentTypeHandle A23;
        public DynamicComponentTypeHandle A24;
        public DynamicComponentTypeHandle A25;
        public DynamicComponentTypeHandle A26;
        public DynamicComponentTypeHandle A27;
        public DynamicComponentTypeHandle A28;
        public DynamicComponentTypeHandle A29;
        public DynamicComponentTypeHandle A30;
        public DynamicComponentTypeHandle A31;
        public DynamicComponentTypeHandle A32;
        public DynamicComponentTypeHandle A33;
        public DynamicComponentTypeHandle A34;
        public DynamicComponentTypeHandle A35;
        public DynamicComponentTypeHandle A36;
        public DynamicComponentTypeHandle A37;
        public DynamicComponentTypeHandle A38;
        public DynamicComponentTypeHandle A39;
        public DynamicComponentTypeHandle A40;
        public DynamicComponentTypeHandle A41;
        public DynamicComponentTypeHandle A42;
        public DynamicComponentTypeHandle A43;
        public DynamicComponentTypeHandle A44;
        public DynamicComponentTypeHandle A45;
        public DynamicComponentTypeHandle A46;
        public DynamicComponentTypeHandle A47;
        public DynamicComponentTypeHandle A48;
        public DynamicComponentTypeHandle A49;
        public DynamicComponentTypeHandle A50;
        public DynamicComponentTypeHandle A51;
        public DynamicComponentTypeHandle A52;
        public DynamicComponentTypeHandle A53;
        public DynamicComponentTypeHandle A54;
        public DynamicComponentTypeHandle A55;
        public DynamicComponentTypeHandle A56;
        public DynamicComponentTypeHandle A57;
        public DynamicComponentTypeHandle A58;
        public DynamicComponentTypeHandle A59;
        public DynamicComponentTypeHandle A60;
        public DynamicComponentTypeHandle A61;
        public DynamicComponentTypeHandle A62;
        public DynamicComponentTypeHandle A63;
        public ulong Mapped;

        public static DynamicComponentTypeHandle* Entry(KindHandleSet* set, int kind)
        {
            return (DynamicComponentTypeHandle*)set + kind;
        }

        public static DynamicComponentTypeHandle* Active(KindHandleSet* set, int kind)
        {
            return (DynamicComponentTypeHandle*)set + Capacity + kind;
        }

        public static bool IsMapped(KindHandleSet* set, int kind)
        {
            return (set->Mapped & (1ul << kind)) != 0;
        }

        public static void Put(ref KindHandleSet set, int kind, DynamicComponentTypeHandle entry, DynamicComponentTypeHandle active)
        {
            var pointer = (KindHandleSet*)UnsafeUtility.AddressOf(ref set);
            *Entry(pointer, kind) = entry;
            *Active(pointer, kind) = active;
            pointer->Mapped |= 1ul << kind;
        }

        public static void Update(ref KindHandleSet set, SystemBase system, int kindCount)
        {
            var pointer = (KindHandleSet*)UnsafeUtility.AddressOf(ref set);
            for (var k = 0; k < kindCount; k++)
            {
                if (!IsMapped(pointer, k)) continue;
                Entry(pointer, k)->Update(system);
                Active(pointer, k)->Update(system);
            }
        }
    }
}
