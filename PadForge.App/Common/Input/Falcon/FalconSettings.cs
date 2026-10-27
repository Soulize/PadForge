using System;
using System.Globalization;
using PadForge.Engine;
using PadForge.Engine.Data;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>What the handle does on its own and what Left/Right and
    /// Up/Down report (issue #521). The settings file stores the number, so
    /// the values stay as numbered.</summary>
    public enum FalconFeel
    {
        /// <summary>FalconJoystick's behavior: a centering spring under a
        /// stick that follows the handle's position.</summary>
        Joystick = 0,
        /// <summary>FalconJoyForce's behavior: a free handle under a stick
        /// that follows its speed, with a push zone at the edge.</summary>
        Aim = 1,
    }

    /// <summary>
    /// One slot's Falcon settings as the haptic thread reads them (issue
    /// #521), parsed from the slot's <see cref="PadSetting"/> on the poll
    /// thread and handed over whole.
    /// </summary>
    internal sealed record FalconSettings(
        FalconFeel Feel,
        double Spring,
        int KickButton,
        double Kick,
        double Shake,
        int BraceButton,
        int PreloadButton,
        double Gravity,
        double ForceCap,
        double Overall)
    {
        public const string DefaultFeel = "0";
        public const string DefaultSpring = "100";
        public const string DefaultKickButton = "1";
        public const string DefaultKick = "100";
        public const string DefaultShake = "100";
        public const string DefaultBraceButton = "0";
        public const string DefaultPreloadButton = "0";
        public const string DefaultGravity = "100";
        public const string DefaultForceCap = "8";

        /// <summary>The Falcon's rated force in CHAI3D's device table
        /// (<c>CDeltaDevices.cpp</c>, the Falcon entry), the default and the
        /// ceiling of the force cap.</summary>
        public const double RatedForceN = 8.0;

        /// <summary>A Falcon no slot is assigned: gravity compensation and
        /// nothing else.</summary>
        public static readonly FalconSettings Unassigned =
            new(FalconFeel.Joystick, 1.0, 1, 1.0, 1.0, 0, 0, 1.0, RatedForceN, 0.0);

        public static FalconSettings From(PadSetting ps)
        {
            if (ps == null) return Unassigned;
            return new FalconSettings(
                ParseInt(ps.FalconFeel, 0) == 1 ? FalconFeel.Aim : FalconFeel.Joystick,
                Math.Clamp(ParseInt(ps.FalconSpring, 100), 0, 200) / 100.0,
                Math.Clamp(ParseInt(ps.FalconKickButton, 1), 0, FalconDevice.ButtonCount),
                Math.Clamp(ParseInt(ps.FalconKick, 100), 0, 200) / 100.0,
                Math.Clamp(ParseInt(ps.FalconShake, 100), 0, 200) / 100.0,
                Math.Clamp(ParseInt(ps.FalconBraceButton, 0), 0, FalconDevice.ButtonCount),
                Math.Clamp(ParseInt(ps.FalconPreloadButton, 0), 0, FalconDevice.ButtonCount),
                Math.Clamp(ParseInt(ps.FalconGravity, 100), 0, 200) / 100.0,
                Math.Clamp(ParseDouble(ps.FalconForceCap, RatedForceN), 1.0, RatedForceN),
                Math.Clamp(ParseInt(ps.ForceOverall, 100), 0, 100) / 100.0);
        }

        /// <summary>A slot's settings as a shared Falcon's frame carries them
        /// to the PC it is plugged into (#521, Remote Link).</summary>
        public static PadForge.Engine.RemoteLink.OutputEffectCodec.FalconTail ToWire(PadSetting ps)
        {
            var s = From(ps);
            static byte Percent(double v) => (byte)Math.Clamp((int)Math.Round(v * 100.0), 0, 200);
            return new(true, (byte)s.Feel, Percent(s.Spring), (byte)s.KickButton, Percent(s.Kick), Percent(s.Shake),
                (byte)s.BraceButton, (byte)s.PreloadButton, Percent(s.Gravity),
                (ushort)Math.Round(s.ForceCap * 10.0), Percent(s.Overall));
        }

        /// <summary>The settings a shared Falcon's frame carried, held to the
        /// ranges <see cref="From"/> keeps.</summary>
        public static FalconSettings FromWire(PadForge.Engine.RemoteLink.OutputEffectCodec.FalconTail t)
            => new(t.Feel == 1 ? FalconFeel.Aim : FalconFeel.Joystick,
                Math.Min((int)t.Spring, 200) / 100.0,
                Math.Min((int)t.KickButton, FalconDevice.ButtonCount),
                Math.Min((int)t.Kick, 200) / 100.0,
                Math.Min((int)t.Shake, 200) / 100.0,
                Math.Min((int)t.BraceButton, FalconDevice.ButtonCount),
                Math.Min((int)t.PreloadButton, FalconDevice.ButtonCount),
                Math.Min((int)t.Gravity, 200) / 100.0,
                Math.Clamp(t.ForceCapTenths / 10.0, 1.0, RatedForceN),
                Math.Min((int)t.Overall, 100) / 100.0);

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static double ParseDouble(string s, double fallback)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;
    }

    /// <summary>
    /// The game's force for one Falcon at one poll (issue #521): the slot's
    /// motors after PadForge's strength, motor swap, audio mix and trigger
    /// fold, with the Overall strength taken back out so the force model can
    /// apply it to newtons, and the directional or condition effect an
    /// Extended slot's game is playing. Immutable, so the haptic thread reads
    /// it without a lock.
    /// </summary>
    internal sealed class FalconGameForce
    {
        public static readonly FalconGameForce Silent = new(0, 0, false, 0, 0, 0, 0, null, 0);

        /// <summary>The heavy (left) and light (right) motors, 0 to 1.</summary>
        public double Heavy { get; }
        public double Light { get; }

        public bool HasEffect { get; }
        public uint EffectType { get; }
        /// <summary>Signed, -1 to 1, with the game's device gain applied.</summary>
        public double Magnitude { get; }
        /// <summary>Where the force comes from, in radians, 0 north (up on
        /// the handle) turning clockwise, as SDL and HID PID define it.</summary>
        public double DirectionRad { get; }
        public uint PeriodMs { get; }
        /// <summary>Condition axes, X then Y, with the game's device gain
        /// in <see cref="ConditionGain"/>. Null without a condition.</summary>
        public ConditionAxisData[] Axes { get; }
        public double ConditionGain { get; }

        public FalconGameForce(double heavy, double light, bool hasEffect, uint effectType, double magnitude,
            double directionRad, uint periodMs, ConditionAxisData[] axes, double conditionGain)
        {
            Heavy = Math.Clamp(heavy, 0, 1);
            Light = Math.Clamp(light, 0, 1);
            HasEffect = hasEffect;
            EffectType = effectType;
            Magnitude = Math.Clamp(magnitude, -1, 1);
            DirectionRad = directionRad;
            PeriodMs = periodMs;
            Axes = axes;
            ConditionGain = Math.Clamp(conditionGain, 0, 1);
        }

        public bool IsCondition => HasEffect && Axes != null && Axes.Length > 0
            && EffectType is FfbEffectTypes.Spring or FfbEffectTypes.Damper
                or FfbEffectTypes.Inertia or FfbEffectTypes.Friction;

        /// <summary>A game's spring plays, which takes the place of the
        /// Joystick feel's centering spring.</summary>
        public bool GameSpring => IsCondition && EffectType == FfbEffectTypes.Spring;

        /// <summary>The snapshot for a combined vibration, the copy-out Step 2
        /// makes for every direct writer. <paramref name="overall"/> is the
        /// Overall strength the motors already carry, 0 to 1.</summary>
        public static FalconGameForce From(Vibration v, ushort left, ushort right, double overall)
        {
            if (v == null) return Silent;
            double unscale = overall > 0.001 ? 1.0 / overall : 0.0;
            double heavy = left / 65535.0 * unscale;
            double light = right / 65535.0 * unscale;
            bool condition = v.HasConditionData && v.ConditionAxisCount > 0 && v.ConditionAxes != null
                && v.EffectType is FfbEffectTypes.Spring or FfbEffectTypes.Damper
                    or FfbEffectTypes.Inertia or FfbEffectTypes.Friction;
            ConditionAxisData[] axes = null;
            if (condition)
            {
                axes = new ConditionAxisData[Math.Min(v.ConditionAxisCount, v.ConditionAxes.Length)];
                Array.Copy(v.ConditionAxes, axes, axes.Length);
            }
            bool has = condition || v.HasDirectionalData;
            double gain = v.DeviceGain / 255.0;
            return new FalconGameForce(heavy, light, has, has ? v.EffectType : 0,
                v.SignedMagnitude / 10000.0 * gain,
                v.Direction / 32767.0 * 2.0 * Math.PI, v.Period, axes, gain);
        }
    }
}
