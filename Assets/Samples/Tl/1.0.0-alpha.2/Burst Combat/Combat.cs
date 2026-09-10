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
        public static void Seek(
            in Frame<AnimationTrack, AnimationClip> frame,
            in FighterPose currentPose,
            ref FighterPose nextPose)
        {
            nextPose.X = currentPose.X + frame.Direction * frame.Clip.X;
            nextPose.Y = currentPose.Y + frame.Direction * frame.Clip.Y;
        }
    }

    public readonly struct DamageTrack
    {
        public static void Seek(
            in Frame<DamageTrack, DamageClip> frame,
            ref CombatStats combat)
        {
            combat.Health -= frame.Direction * frame.Clip.Amount;
        }
    }
}
