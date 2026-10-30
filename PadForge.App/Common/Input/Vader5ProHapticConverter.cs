using System;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// DualSense Fidelity: source-faithful PCM amplitude estimation for the
    /// Vader's amplitude-only motors. No default crest boost, arbitrary
    /// 6/10-ms pulses, gamma expansion or nonzero release tail.
    ///
    /// This estimates an output motor command, NOT the physical DualSense
    /// voice-coil displacement. Frequency response can only be corrected
    /// after calibration with measured actuator responses.
    /// </summary>
    internal sealed class Vader5ProHapticConverter
    {
        internal const int SilenceConfirmMs = 3;
        internal const int GapResetMs = 30;
        internal const int LowBandHz = 120;
        internal const int HighBandHz = 400;
        private const double SilenceSampleFloor = 0.0015;
        private const double OutputFloor = 0.004;

        internal readonly record struct Frame(
            ushort Left, ushort Right, ushort HighLeft, ushort HighRight,
            ushort LowLeft, ushort LowRight,
            byte PulseLeftMs, byte PulseRightMs);

        private struct Channel
        {
            public double Envelope;
            public double HighEnvelope;
            public double LowEnvelope;
            public double LowPole1, LowPole2;
            public double MidPole1, MidPole2;
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

        /// <summary>Calls are serialized by the owning PersonaFeed callback.
        /// Test overrides do not mutate process-wide calibration values.</summary>
        internal Frame Process(ReadOnlySpan<byte> pcm, int stride,
            int leftOffset, int rightOffset, int rateHz, long nowMs,
            Vader5ProFidelitySettings.BandWeights weights = null,
            bool? transientEnhancement = null)
        {
            if (stride < 4 || rateHz < 1000 || leftOffset < 0
                || rightOffset < 0 || leftOffset + 2 > stride
                || rightOffset + 2 > stride || pcm.Length < stride)
            {
                Reset();
                return default;
            }

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
            weights ??= Vader5ProFidelitySettings.CurrentBands;
            bool boost = transientEnhancement
                ?? Vader5ProFidelitySettings.TransientEnhancementEnabled;

            int bucketSize = Math.Max(1, rateHz / 1000);
            int bucketCount = 0;
            double bucketL = 0, bucketR = 0, peakL = 0, peakR = 0;
            double energyL = 0, energyR = 0;
            double lowEnergyL = 0, lowEnergyR = 0;
            double midEnergyL = 0, midEnergyR = 0;
            double highEnergyL = 0, highEnergyR = 0;
            double alphaLow = 1.0 - Math.Exp(-2.0 * Math.PI * LowBandHz / rateHz);
            double alphaHigh = 1.0 - Math.Exp(-2.0 * Math.PI * HighBandHz / rateHz);
            int silenceFrames = Math.Max(1, rateHz * SilenceConfirmMs / 1000);
            bool idleL = _left.Envelope < OutputFloor;
            bool idleR = _right.Envelope < OutputFloor;

            // Use active sample COUNT rather than first-to-last span:
            // separate small taps in the same callback must not fill their
            // intervening silence with an invented long motor command.
            int activeL = 0, activeR = 0;
            for (int i = 0; i < frames; i++)
            {
                int offset = i * stride;
                double l = (short)(pcm[offset + leftOffset]
                    | (pcm[offset + leftOffset + 1] << 8)) / 32768.0;
                double r = (short)(pcm[offset + rightOffset]
                    | (pcm[offset + rightOffset + 1] << 8)) / 32768.0;
                double le = l * l, re = r * r;
                energyL += le; energyR += re;

                if (Math.Abs(l) > SilenceSampleFloor) activeL++;
                if (Math.Abs(r) > SilenceSampleFloor) activeR++;

                highEnergyL += Step(ref _left, l, alphaLow, alphaHigh,
                    silenceFrames, out double lowL, out double midL);
                highEnergyR += Step(ref _right, r, alphaLow, alphaHigh,
                    silenceFrames, out double lowR, out double midR);
                lowEnergyL += lowL; lowEnergyR += lowR;
                midEnergyL += midL; midEnergyR += midR;

                // Evaluate peak buckets only if explicitly opting in to
                // extra impact emphasis; default Fidelity pays no peak cost.
                if (boost)
                {
                    bucketL += le; bucketR += re; bucketCount++;
                    if (bucketCount >= bucketSize)
                    {
                        peakL = Math.Max(peakL, Math.Sqrt(bucketL / bucketCount));
                        peakR = Math.Max(peakR, Math.Sqrt(bucketR / bucketCount));
                        bucketL = bucketR = 0;
                        bucketCount = 0;
                    }
                }
            }
            if (boost && bucketCount >= Math.Max(1, bucketSize / 2))
            {
                peakL = Math.Max(peakL, Math.Sqrt(bucketL / bucketCount));
                peakR = Math.Max(peakR, Math.Sqrt(bucketR / bucketCount));
            }

            double rawRmsL = Math.Sqrt(energyL / frames);
            double rawRmsR = Math.Sqrt(energyR / frames);
            double levelL = Vader5ProFidelitySettings.PerceptualRms(
                energyL / frames, lowEnergyL / frames,
                midEnergyL / frames, highEnergyL / frames, weights);
            double levelR = Vader5ProFidelitySettings.PerceptualRms(
                energyR / frames, lowEnergyR / frames,
                midEnergyR / frames, highEnergyR / frames, weights);
            if (boost)
            {
                levelL = Math.Min(1.0, levelL
                    + Math.Max(0.0, PeakEnhanced(rawRmsL, peakL) - rawRmsL));
                levelR = Math.Min(1.0, levelR
                    + Math.Max(0.0, PeakEnhanced(rawRmsR, peakR) - rawRmsR));
            }

            double highL = Math.Sqrt(highEnergyL / frames);
            double highR = Math.Sqrt(highEnergyR / frames);
            double lowBandL = Math.Sqrt(lowEnergyL / frames);
            double lowBandR = Math.Sqrt(lowEnergyR / frames);

            // If audio started and went silent within one callback, preserve
            // its proportional RMS and its observed non-silent span. A pulse
            // cannot be replayed retroactively at sample accuracy: the
            // vendor command is one amplitude byte, so only the measured
            // duration is represented (minimum 3 ms for the HID edge gap).
            // Do not synthesize a constant 10 ms "impact" from a brief cue.
            bool endedL = _left.SilentFrames >= silenceFrames;
            bool endedR = _right.SilentFrames >= silenceFrames;
            bool pulseL = idleL && endedL && activeL > 0 && levelL >= OutputFloor;
            bool pulseR = idleR && endedR && activeR > 0 && levelR >= OutputFloor;
            byte pulseLeftMs = pulseL ? PulseDuration(activeL, rateHz) : (byte)0;
            byte pulseRightMs = pulseR ? PulseDuration(activeR, rateHz) : (byte)0;

            if (endedL) _left = default;
            else
            {
                // Zero additional attack/release envelope in Fidelity mode.
                // Do not mask quiet texture under an invented slow attack.
                _left.Envelope = levelL;
                _left.HighEnvelope = highL;
                _left.LowEnvelope = lowBandL;
            }
            if (endedR) _right = default;
            else
            {
                _right.Envelope = levelR;
                _right.HighEnvelope = highR;
                _right.LowEnvelope = lowBandR;
            }

            return new Frame(
                ToLevel(pulseL ? levelL : _left.Envelope),
                ToLevel(pulseR ? levelR : _right.Envelope),
                ToLevel(pulseL ? highL : _left.HighEnvelope),
                ToLevel(pulseR ? highR : _right.HighEnvelope),
                ToLevel(pulseL ? lowBandL : _left.LowEnvelope),
                ToLevel(pulseR ? lowBandR : _right.LowEnvelope),
                pulseLeftMs, pulseRightMs);
        }

        private static byte PulseDuration(int activeSamples, int rateHz)
        {
            long duration = ((long)activeSamples * 1000 + rateHz - 1) / rateHz;
            return (byte)Math.Clamp(duration,
                FlydigiVader5ProHidWriter.StopEdgeMinIntervalMs, byte.MaxValue);
        }

        private static double Step(ref Channel state, double input,
            double alphaLow, double alphaHigh, int silenceFrames,
            out double lowEnergy, out double midEnergy)
        {
            if (Math.Abs(input) <= SilenceSampleFloor)
            {
                if (state.SilentFrames < silenceFrames) state.SilentFrames++;
            }
            else state.SilentFrames = 0;

            // Low <~120 Hz, mid ~120-400 Hz, high >~400 Hz.
            // These are low-latency crossovers, not pitch detection; each
            // branch is normalized by total band energy before weighting.
            state.LowPole1 += alphaLow * (input - state.LowPole1);
            state.LowPole2 += alphaLow * (state.LowPole1 - state.LowPole2);
            state.MidPole1 += alphaHigh * (input - state.MidPole1);
            state.MidPole2 += alphaHigh * (state.MidPole1 - state.MidPole2);
            double low = state.LowPole2;
            double mid = state.MidPole2 - state.LowPole2;
            double high = input - state.MidPole2;
            lowEnergy = low * low;
            midEnergy = mid * mid;
            return high * high;
        }

        /// <summary>Only called for explicit, opt-in "Impact boost".</summary>
        private static double PeakEnhanced(double rms, double peak)
        {
            double a = SmoothStep(0.12, 0.35, peak);
            double b = SmoothStep(1.25, 1.9, peak / (rms + 0.0001));
            return Math.Clamp(rms + Math.Max(0.0, peak * 0.85 - rms) * a * b,
                0.0, 1.0);
        }

        private static double SmoothStep(double low, double high, double x)
        {
            double t = Math.Clamp((x - low) / (high - low), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private static ushort ToLevel(double amplitude)
            => amplitude < OutputFloor ? (ushort)0
                : (ushort)Math.Clamp((int)Math.Round(amplitude * 65535.0),
                    0, 65535);
    }

    /// <summary>Legacy gain/gamma/floor controls retained for compatibility.
    /// Leave at 100/100/0 for fidelity, then tune device response with LUT.</summary>
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
