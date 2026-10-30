using System;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Vader 5 Pro-only PCM to amplitude conversion. No haptic samples are
    /// buffered for playback: each call reduces the current audio frame.
    /// Stereo location is retained; spectral information is an optional
    /// supplementary trigger source and never replaces native trigger data.
    /// All state is owned by a single PersonaFeed callback gate.
    /// </summary>
    internal sealed class Vader5ProHapticConverter
    {
        internal const int SilenceConfirmMs = 3;
        internal const int GapResetMs = 30;
        internal const int SpectralCutoffHz = 180;
        private const double SilenceSampleFloor = 0.0015;
        // Keep soft authored details. The old 2% cut-off made almost-silent
        // texture disappear and forced users to overboost Gamma/Floor.
        private const double OutputFloor = 0.004;
        private const double ImpactPeakThreshold = 0.18;
        private const double ImpactAmplitudeThreshold = 0.10;

        internal const int TransientBurstHoldMs = 10;
        internal const int TextureHoldMs = 6;
        internal readonly record struct Frame(ushort Left, ushort Right,
            ushort HighLeft, ushort HighRight, ushort LowLeft, ushort LowRight,
            byte BurstMask, byte TextureMask);

        private struct Channel
        {
            public double Envelope;
            public double HighEnvelope;
            public double LowEnvelope;
            public double LowPass1;
            public double LowPass2;
            public int SilentFrames;
        }

        private Channel _left, _right;
        private int _rate, _stride, _leftOffset, _rightOffset;
        private long _lastTimeMs = -1;

        internal void Reset()
        {
            _left = default;
            _right = default;
            _rate = _stride = _leftOffset = _rightOffset = 0;
            _lastTimeMs = -1;
        }

        internal Frame Process(ReadOnlySpan<byte> pcm, int stride,
            int leftOffset, int rightOffset, int rateHz, long nowMs)
        {
            if (stride < 4 || rateHz < 1000 || leftOffset < 0
                || rightOffset < 0 || leftOffset + 2 > stride
                || rightOffset + 2 > stride || pcm.Length < stride)
            {
                Reset();
                return default;
            }

            // State from a closed/reopened stream must not generate a phantom
            // pulse. Format changes include channel routing, not just sample rate.
            if (_lastTimeMs >= 0 && (nowMs < _lastTimeMs
                || nowMs - _lastTimeMs > GapResetMs
                || rateHz != _rate || stride != _stride
                || leftOffset != _leftOffset || rightOffset != _rightOffset))
                Reset();

            _lastTimeMs = nowMs;
            _rate = rateHz;
            _stride = stride;
            _leftOffset = leftOffset;
            _rightOffset = rightOffset;

            int frames = pcm.Length / stride;
            if (frames == 0) return default;
            int bucketSize = Math.Max(1, rateHz / 1000); // 1 ms peak-RMS buckets
            int bucketCount = 0;
            double bucketL = 0, bucketR = 0, peakL = 0, peakR = 0;
            double energyL = 0, energyR = 0, highEnergyL = 0, highEnergyR = 0;
            double lowEnergyL = 0, lowEnergyR = 0;
            double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * SpectralCutoffHz / rateHz);
            int silenceFrames = Math.Max(1, rateHz * SilenceConfirmMs / 1000);
            bool leftWasIdle = _left.Envelope < OutputFloor;
            bool rightWasIdle = _right.Envelope < OutputFloor;

            for (int i = 0; i < frames; ++i)
            {
                int offset = i * stride;
                double l = (short)(pcm[offset + leftOffset]
                    | (pcm[offset + leftOffset + 1] << 8)) / 32768.0;
                double r = (short)(pcm[offset + rightOffset]
                    | (pcm[offset + rightOffset + 1] << 8)) / 32768.0;
                double le = l * l, re = r * r;
                energyL += le; energyR += re;
                bucketL += le; bucketR += re;
                bucketCount++;

                highEnergyL += Step(ref _left, l, alpha, silenceFrames, out double lowSampleL);
                highEnergyR += Step(ref _right, r, alpha, silenceFrames, out double lowSampleR);
                lowEnergyL += lowSampleL;
                lowEnergyR += lowSampleR;

                if (bucketCount >= bucketSize)
                {
                    peakL = Math.Max(peakL, Math.Sqrt(bucketL / bucketCount));
                    peakR = Math.Max(peakR, Math.Sqrt(bucketR / bucketCount));
                    bucketL = bucketR = 0;
                    bucketCount = 0;
                }
            }
            if (bucketCount >= Math.Max(1, bucketSize / 2))
            {
                peakL = Math.Max(peakL, Math.Sqrt(bucketL / bucketCount));
                peakR = Math.Max(peakR, Math.Sqrt(bucketR / bucketCount));
            }

            // Quiet texture uses its actual whole-callback RMS. Formerly
            // max(RMS, 0.85 * 1ms peak) * 1.4 promoted fine cloth/footstep
            // taps to full transient bursts. Only a concentrated, sufficiently
            // loud impact earns a smoothly gated peak contribution.
            double immediateL = ShapeAmplitude(Math.Sqrt(energyL / frames), peakL);
            double immediateR = ShapeAmplitude(Math.Sqrt(energyR / frames), peakR);
            double highL = Math.Min(1.0, Math.Sqrt(highEnergyL / frames));
            double highR = Math.Min(1.0, Math.Sqrt(highEnergyR / frames));
            double lowL = Math.Min(1.0, Math.Sqrt(lowEnergyL / frames));
            double lowR = Math.Min(1.0, Math.Sqrt(lowEnergyR / frames));
            double seconds = frames / (double)rateHz;
            // A pulse can begin and end inside a single audio callback.
            // Real impacts keep their existing fast 10ms hold. Quiet texture
            // stays quiet: publish its proportional window RMS for at most
            // 6ms rather than creating a 10ms hard kick on every footstep.
            bool pulseL = leftWasIdle && _left.SilentFrames >= silenceFrames
                && immediateL >= OutputFloor;
            bool pulseR = rightWasIdle && _right.SilentFrames >= silenceFrames
                && immediateR >= OutputFloor;
            bool burstL = pulseL && peakL >= ImpactPeakThreshold
                && immediateL >= ImpactAmplitudeThreshold;
            bool burstR = pulseR && peakR >= ImpactPeakThreshold
                && immediateR >= ImpactAmplitudeThreshold;
            bool textureL = pulseL && !burstL;
            bool textureR = pulseR && !burstR;
            UpdateEnvelope(ref _left, immediateL, highL, lowL, seconds, silenceFrames);
            UpdateEnvelope(ref _right, immediateR, highR, lowR, seconds, silenceFrames);

            return new Frame(
                ToLevel(pulseL ? immediateL : _left.Envelope),
                ToLevel(pulseR ? immediateR : _right.Envelope),
                ToLevel(pulseL ? highL : _left.HighEnvelope),
                ToLevel(pulseR ? highR : _right.HighEnvelope),
                ToLevel(pulseL ? lowL : _left.LowEnvelope),
                ToLevel(pulseR ? lowR : _right.LowEnvelope),
                (byte)((burstL ? 1 : 0) | (burstR ? 2 : 0)),
                (byte)((textureL ? 1 : 0) | (textureR ? 2 : 0)));
        }

        private static double Step(ref Channel state, double value,
            double alpha, int silenceFrames, out double lowEnergy)
        {
            if (Math.Abs(value) <= SilenceSampleFloor)
            {
                if (state.SilentFrames < silenceFrames)
                    state.SilentFrames++;
            }
            else
            {
                state.SilentFrames = 0;
            }

            // Two-pole IIR low-pass; the residual is the high band.
            // Unlike an 83 ms pitch-detection ring, this introduces no
            // audio-history-sized output delay and allocates nothing per tick.
            state.LowPass1 += alpha * (value - state.LowPass1);
            state.LowPass2 += alpha * (state.LowPass1 - state.LowPass2);
            double high = value - state.LowPass2;
            lowEnergy = state.LowPass2 * state.LowPass2;
            return high * high;
        }

        private static void UpdateEnvelope(ref Channel state, double target,
            double highTarget, double lowTarget, double seconds, int silenceFrames)
        {
            if (state.SilentFrames >= silenceFrames)
            {
                // A real sustained silent interval is a stop command, not
                // another point in an exponentially decaying RMS tail.
                state = default;
                return;
            }
            state.Envelope = Follow(state.Envelope, target, seconds);
            state.HighEnvelope = Follow(state.HighEnvelope, highTarget, seconds);
            state.LowEnvelope = Follow(state.LowEnvelope, lowTarget, seconds);
        }

        /// <summary>Transient boost only for a genuine loud, concentrated
        /// event. Zero gain for faint PCM irrespective of the crest factor.
        /// Both gates are smooth to avoid a new audible/tactile step.</summary>
        private static double ShapeAmplitude(double rms, double oneMsPeak)
        {
            double peakGate = SmoothStep(0.12, 0.35, oneMsPeak);
            double crestGate = SmoothStep(1.25, 1.9,
                oneMsPeak / (rms + 0.0001));
            double impactContribution = Math.Max(0.0, oneMsPeak * 0.85 - rms)
                * peakGate * crestGate;
            return Math.Clamp(rms + impactContribution, 0.0, 1.0);
        }

        private static double SmoothStep(double min, double max, double value)
        {
            double x = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }

        private static double Follow(double current, double target, double seconds)
        {
            // The old unconditional 1.5 ms attack accentuated each small
            // fabric/footstep cue. Gentle haptics ramp without losing the
            // rapid rise of high-energy impacts.
            double attack = target <= 0.12 ? 0.012 :
                target >= 0.30 ? 0.0015 :
                0.012 - (target - 0.12) / 0.18 * 0.0105;
            const double release = 0.008;
            double tau = target >= current ? attack : release;
            return Math.Clamp(current + (target - current)
                * (1.0 - Math.Exp(-seconds / tau)), 0.0, 1.0);
        }

        private static ushort ToLevel(double amplitude)
            => amplitude < OutputFloor ? (ushort)0
                : (ushort)Math.Clamp((int)Math.Round(amplitude * 65535.0), 0, 65535);
    }

    /// <summary>
    /// Per-motor 8-bit calibration, applied only to the native 0x12 writer.
    /// Defaults are identity; a zero intent stays zero even with a drive floor.
    /// A single immutable profile snapshot avoids tearing on the HID worker.
    /// </summary>
    internal static class Vader5ProMotorCalibration
    {
        internal readonly record struct Curve(int GainPercent, int GammaPercent, int MinDrive);
        internal sealed record Profile(Curve GripLeft, Curve GripRight,
            Curve TriggerLeft, Curve TriggerRight)
        {
            internal static Profile Identity { get; } = new(
                new Curve(100, 100, 0), new Curve(100, 100, 0),
                new Curve(100, 100, 0), new Curve(100, 100, 0));
        }

        internal readonly record struct Motors(byte GripLeft, byte GripRight,
            byte TriggerLeft, byte TriggerRight);

        private static Profile _profile = Profile.Identity;
        internal static Profile Current => Volatile.Read(ref _profile);

        internal static void SetProfile(Profile profile)
            => Volatile.Write(ref _profile, profile ?? Profile.Identity);

        internal static string ToCsv(Profile profile)
        {
            profile ??= Profile.Identity;
            static string Part(Curve c) => $"{c.GainPercent},{c.GammaPercent},{c.MinDrive}";
            return string.Join(";", Part(profile.GripLeft), Part(profile.GripRight),
                Part(profile.TriggerLeft), Part(profile.TriggerRight));
        }

        internal static Profile ParseCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Profile.Identity;
            var parts = csv.Split(';');
            if (parts.Length != 4) return Profile.Identity;
            var curves = new Curve[4];
            for (int i = 0; i < 4; i++)
            {
                var values = parts[i].Split(',');
                if (values.Length != 3
                    || !int.TryParse(values[0], out int gain)
                    || !int.TryParse(values[1], out int gamma)
                    || !int.TryParse(values[2], out int floor))
                    return Profile.Identity;
                curves[i] = new Curve(Math.Clamp(gain, 0, 200),
                    Math.Clamp(gamma, 50, 180), Math.Clamp(floor, 0, 64));
            }
            return new Profile(curves[0], curves[1], curves[2], curves[3]);
        }

        internal static byte Encode(ushort level, Curve curve)
        {
            if (level == 0 || curve.GainPercent <= 0) return 0;
            double x = level / 65535.0;
            double gamma = Math.Clamp(curve.GammaPercent, 50, 180) / 100.0;
            double gain = Math.Clamp(curve.GainPercent, 0, 200) / 100.0;
            int mapped = Math.Clamp((int)Math.Round(
                Math.Pow(x, gamma) * gain * 255.0), 0, 255);
            int floor = Math.Clamp(curve.MinDrive, 0, 64);
            return (byte)Math.Max(mapped, floor);
        }

        internal static Motors Apply(ushort gripL, ushort gripR,
            ushort triggerL, ushort triggerR, Profile profile)
        {
            profile ??= Profile.Identity;
            return new Motors(Encode(gripL, profile.GripLeft),
                Encode(gripR, profile.GripRight),
                Encode(triggerL, profile.TriggerLeft),
                Encode(triggerR, profile.TriggerRight));
        }
    }

    /// <summary>
    /// Native trigger data (including adaptive trigger translation) always
    /// outranks optional PCM spectral assist. The latter fills only an idle
    /// trigger channel. Grip content stays in its original left/right lanes.
    /// </summary>
    internal static class Vader5ProHapticMixer
    {
        internal readonly record struct MotorLevels(ushort GripLeft,
            ushort GripRight, ushort TriggerLeft, ushort TriggerRight);

        internal static MotorLevels Mix(ushort gripLeft, ushort gripRight,
            ushort triggerLeft, ushort triggerRight, ushort spectralLeft,
            ushort spectralRight, bool spectralAssist)
        {
            return new MotorLevels(gripLeft, gripRight,
                triggerLeft != 0 ? triggerLeft : spectralAssist ? spectralLeft : (ushort)0,
                triggerRight != 0 ? triggerRight : spectralAssist ? spectralRight : (ushort)0);
        }
    }
}
