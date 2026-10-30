using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Independent synthetic-signal acceptance for the Vader-only
    /// haptic renderer. The normal DualSense/SDL paths must remain unchanged.</summary>
    public class Vader5ProEnhancedHapticsTests
    {
        private static byte[] Tone(int rate, int durationMs, double hz,
            double left = 0.7, double right = 0.0)
        {
            int samples = rate * durationMs / 1000;
            byte[] pcm = new byte[samples * 4];
            for (int i = 0; i < samples; i++)
            {
                short l = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * left * 32767);
                short r = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * right * 32767);
                int off = i * 4;
                pcm[off] = (byte)l; pcm[off + 1] = (byte)(l >> 8);
                pcm[off + 2] = (byte)r; pcm[off + 3] = (byte)(r >> 8);
            }
            return pcm;
        }

        [Fact]
        public void SustainedSine_ZeroCrossingsAreNotMistakenForStop()
        {
            var converter = new Vader5ProHapticConverter();
            for (int i = 0; i < 12; i++)
            {
                var frame = converter.Process(Tone(48000, 10, 80), 4, 0, 2, 48000, i * 10L);
                Assert.True(frame.Left > 0, "Continuous 80 Hz vibration must not stop");
                Assert.Equal((ushort)0, frame.Right);
            }
        }

        [Fact]
        public void Silence_StopsEachChannelIndependentlyWithinFourMilliseconds()
        {
            var converter = new Vader5ProHapticConverter();
            var on = converter.Process(Tone(48000, 10, 160, 0.8, 0.8), 4, 0, 2, 48000, 10);
            Assert.True(on.Left > 0 && on.Right > 0);
            var oneSide = converter.Process(Tone(48000, 5, 160, 0.0, 0.8), 4, 0, 2, 48000, 15);
            Assert.Equal((ushort)0, oneSide.Left);
            Assert.True(oneSide.Right > 0);
            var stop = converter.Process(new byte[48000 * 5 / 1000 * 4], 4, 0, 2, 48000, 20);
            Assert.Equal((ushort)0, stop.Left);
            Assert.Equal((ushort)0, stop.Right);
        }

        [Fact]
        public void ShortImpact_IsNotBuriedByLongLoudnessSmoothing()
        {
            var converter = new Vader5ProHapticConverter();
            var pulse = converter.Process(Tone(48000, 5, 220, 0.9), 4, 0, 2, 48000, 5);
            Assert.True(pulse.Left > 25000, "5 ms impact must remain prominent");
            var stop = converter.Process(new byte[48000 * 5 / 1000 * 4], 4, 0, 2, 48000, 10);
            Assert.Equal((ushort)0, stop.Left);
        }

        [Fact]
        public void ResumedSilentStream_NeverReplaysOldEnvelope()
        {
            var converter = new Vader5ProHapticConverter();
            Assert.True(converter.Process(Tone(48000, 10, 180), 4, 0, 2, 48000, 10).Left > 0);
            var afterGap = converter.Process(new byte[48000 * 5 / 1000 * 4], 4, 0, 2, 48000, 70);
            Assert.Equal((ushort)0, afterGap.Left);
        }

        [Fact]
        public void FormatChange_ClearsOldFilterState()
        {
            var converter = new Vader5ProHapticConverter();
            Assert.True(converter.Process(Tone(48000, 10, 200), 4, 0, 2, 48000, 10).Left > 0);
            var afterFormat = converter.Process(new byte[32000 * 5 / 1000 * 4], 4, 0, 2, 32000, 15);
            Assert.Equal((ushort)0, afterFormat.Left);
        }

        [Fact]
        public void SpectralAnalysis_SeparatesLowFromHigh_AndKeepsStereo()
        {
            var low = new Vader5ProHapticConverter();
            var high = new Vader5ProHapticConverter();
            Vader5ProHapticConverter.Frame lf = default, hf = default;
            for (int i = 0; i < 8; i++)
            {
                lf = low.Process(Tone(48000, 10, 80), 4, 0, 2, 48000, i * 10L);
                hf = high.Process(Tone(48000, 10, 700), 4, 0, 2, 48000, i * 10L);
            }
            Assert.True(hf.HighLeft > lf.HighLeft,
                "High-frequency energy of a 700 Hz tone must exceed 80 Hz");
            Assert.Equal((ushort)0, hf.HighRight);
            Assert.Equal((ushort)0, lf.HighRight);
        }

        [Fact]
        public void Calibration_PreservesZero_IsMonotone_AndMotorSpecific()
        {
            var linear = new Vader5ProMotorCalibration.Curve(100, 100, 0);
            var boosted = new Vader5ProMotorCalibration.Curve(150, 75, 12);
            Assert.Equal((byte)0, Vader5ProMotorCalibration.Encode(0, boosted));
            Assert.Equal((byte)128, Vader5ProMotorCalibration.Encode(32768, linear));
            byte previous = 0;
            for (int i = 0; i <= 65535; i += 257)
            {
                var current = Vader5ProMotorCalibration.Encode((ushort)i, boosted);
                Assert.True(current >= previous, "Calibration must be monotonic");
                previous = current;
            }
            var profile = new Vader5ProMotorCalibration.Profile(
                boosted, linear, linear, linear);
            var output = Vader5ProMotorCalibration.Apply(32768, 32768, 32768, 32768, profile);
            Assert.True(output.GripLeft > output.GripRight);
            Assert.Equal(output.GripRight, output.TriggerLeft);
            Assert.Equal(output.TriggerLeft, output.TriggerRight);
        }

        [Fact]
        public void Mixer_NativeTriggerOutranksSpectralAssist_PerSide()
        {
            var mixed = Vader5ProHapticMixer.Mix(
                30000, 24000, 12000, 0, 45000, 40000, true);
            Assert.Equal((ushort)30000, mixed.GripLeft);
            Assert.Equal((ushort)24000, mixed.GripRight);
            Assert.Equal((ushort)12000, mixed.TriggerLeft);
            Assert.Equal((ushort)40000, mixed.TriggerRight);

            var disabled = Vader5ProHapticMixer.Mix(0, 0, 0, 0, 45000, 40000, false);
            Assert.Equal((ushort)0, disabled.TriggerLeft);
            Assert.Equal((ushort)0, disabled.TriggerRight);
        }
    }
}
