using Tl.Pack.Inventory;
using Unity.Entities;
using UnityEngine;

namespace TlPacks.Inventory
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Tl/Bag")]
    public sealed class TlBagAuthoring : MonoBehaviour
    {
        [Tooltip("Items in the bag before any timeline plays. Inventory clips and Pickup markers add to or take from these.")]
        public int Wood;
        public int Stone;
        public int Plank;
        public int Potion;
        public int Sword;
        public int Coin;
        public int Key;
        public int Gem;
    }

    public sealed class TlBagBaker : Baker<TlBagAuthoring>
    {
        public override void Bake(TlBagAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new Bag
            {
                Wood = authoring.Wood,
                Stone = authoring.Stone,
                Plank = authoring.Plank,
                Potion = authoring.Potion,
                Sword = authoring.Sword,
                Coin = authoring.Coin,
                Key = authoring.Key,
                Gem = authoring.Gem,
            });
        }
    }
}
