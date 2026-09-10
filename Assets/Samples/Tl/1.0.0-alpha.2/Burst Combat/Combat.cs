using Tl;

namespace Tl.Samples.BurstCombat
{
    public struct FighterPose
    {
        public float X;
        public float Y;
    }

    public struct CombatStats
    {
        public float Health;
    }

    public readonly struct AnimationClip
    {
        public readonly float X;
        public readonly float Y;

        public AnimationClip(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public readonly struct DamageClip
    {
        public readonly float Amount;

        public DamageClip(float amount)
        {
            Amount = amount;
        }
    }

    public readonly struct AnimationTrack
    {
        public static void Forward(in AnimationTrack track, in AnimationClip clip, ClipState state, uint tick, in FighterPose currentPose, ref FighterPose nextPose)
        {
            nextPose.X = currentPose.X + clip.X;
            nextPose.Y = currentPose.Y + clip.Y;
        }

        public static void Backward(in AnimationTrack track, in AnimationClip clip, ClipState state, uint tick, in FighterPose currentPose, ref FighterPose nextPose)
        {
            nextPose.X = currentPose.X - clip.X;
            nextPose.Y = currentPose.Y - clip.Y;
        }
    }

    public readonly struct DamageTrack
    {
        public static void Forward(in DamageTrack track, in DamageClip clip, ClipState state, uint tick, ref CombatStats combat)
        {
            combat.Health -= clip.Amount;
        }

        public static void Backward(in DamageTrack track, in DamageClip clip, ClipState state, uint tick, ref CombatStats combat)
        {
            combat.Health += clip.Amount;
        }
    }
}
