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
            Assert.True(lf.LowLeft > hf.LowLeft,
                "80 Hz must carry more low-band energy than 700 Hz");
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

        [Fact]
        public void Scheduler_UrgentStartEdgeDoesNotCompromiseNormalCoalescing()
        {
            var silent = new FlydigiVader5ProHidWriter.Motors(0, 0, 0, 0);
            var pulse = new FlydigiVader5ProHidWriter.Motors(200, 0, 0, 0);
            Assert.True(FlydigiVader5ProHidWriter.HasStartingMotor(silent, pulse));
            Assert.False(FlydigiVader5ProHidWriter.HasStartingMotor(pulse, pulse));
            Assert.Equal(30, FlydigiVader5ProHidWriter.DelayUntilReport(
                silent, pulse, 1000, 1000, true, 30));
            Assert.Equal(3, FlydigiVader5ProHidWriter.DelayUntilReport(
                silent, pulse, 1000, 1000, true, 30, urgentStarts: true));
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                silent, pulse, 1003, 1000, true, 30, urgentStarts: true));
        }

        [Fact]
        public void WholePulseInsideOneCallback_IsPublishedAndBounded()
        {
            var converter = new Vader5ProHapticConverter();
            var hostChunk = new byte[48000 * 12 / 1000 * 4];
            var impact = Tone(48000, 3, 500, 0.8, 0.0);
            Array.Copy(impact, hostChunk, impact.Length);
            var frame = converter.Process(hostChunk, 4, 0, 2, 48000, 12);
            Assert.InRange(frame.Left, (ushort)16000, (ushort)22000);
            Assert.Equal(3, frame.PulseLeftMs); // Actual 3 ms duration.
            Assert.Equal(0, frame.PulseRightMs);
            var quiet = converter.Process(new byte[48000 * 5 / 1000 * 4],
                4, 0, 2, 48000, 17);
            Assert.Equal((ushort)0, quiet.Left);
            Assert.Equal(0, quiet.PulseLeftMs);
        }

        [Theory]
        [InlineData(0.03)]
        [InlineData(0.05)]
        [InlineData(0.09)]
        public void QuietTexture_InOneCallback_StaysProportionalAndNotAnImpact(double amplitude)
        {
            var converter = new Vader5ProHapticConverter();
            var hostChunk = new byte[48000 * 12 / 1000 * 4];
            var gentle = Tone(48000, 3, 420, amplitude, 0.0);
            Array.Copy(gentle, hostChunk, gentle.Length);
            var frame = converter.Process(hostChunk, 4, 0, 2, 48000, 12);
            Assert.True(frame.Left > 0, "Soft texture must not disappear");
            Assert.True(frame.Left < 6500, "A gentle cue must not become a hard kick");
            Assert.Equal(3, frame.PulseLeftMs);
            Assert.Equal(0, frame.PulseRightMs);
            var motor = Vader5ProMotorCalibration.Encode(frame.Left,
                Vader5ProMotorCalibration.Profile.Identity.GripLeft);
            Assert.InRange(motor, (byte)1, (byte)25);
            Assert.InRange(frame.Left, (ushort)1, (ushort)6500);
        }

        [Fact]
        public void RepeatedQuietFootsteps_NeverGetTransientPeakBoost()
        {
            var converter = new Vader5ProHapticConverter();
            for (int i = 0; i < 30; i++)
            {
                var hostChunk = new byte[48000 * 12 / 1000 * 4];
                var gentle = Tone(48000, 2, 380, 0.07, 0.0);
                Array.Copy(gentle, hostChunk, gentle.Length);
                var frame = converter.Process(hostChunk, 4, 0, 2, 48000, 12L * i);
                Assert.InRange(frame.Left, (ushort)1, (ushort)6000);
                Assert.Equal(3, frame.PulseLeftMs); // 2 ms source, 3 ms HID edge limit.
                Assert.Equal(0, frame.PulseRightMs);
            }
        }

        [Fact]
        public void StrongImpulseStaysProportionalAndUsesMeasuredPulseLength()
        {
            var converter = new Vader5ProHapticConverter();
            var hostChunk = new byte[48000 * 12 / 1000 * 4];
            Array.Copy(Tone(48000, 3, 500, 0.8, 0.0),
                hostChunk, 48000 * 3 / 1000 * 4);
            var frame = converter.Process(hostChunk, 4, 0, 2, 48000, 12);
            Assert.InRange(frame.Left, (ushort)16000, (ushort)22000);
            Assert.Equal(3, frame.PulseLeftMs);
            Assert.Equal(0, frame.PulseRightMs);
        }

        [Fact]
        public void SustainedWeakTexturePreservesDynamicRange()
        {
            var converter = new Vader5ProHapticConverter();
            var weak = converter.Process(Tone(48000, 10, 80, 0.05),
                4, 0, 2, 48000, 10);
            Assert.True(weak.Left > 0);
            Assert.True(weak.Left < 5000);
            var strong = converter.Process(Tone(48000, 10, 80, 0.8),
                4, 0, 2, 48000, 20);
            Assert.True(strong.Left > weak.Left * 4);
        }

        [Fact]
        public void CalibrationCsv_RoundTripsAndRejectsCorruptProfiles()
        {
            var identity = Vader5ProMotorCalibration.Profile.Identity;
            Assert.Equal(identity, Vader5ProMotorCalibration.ParseCsv(""));
            Assert.Equal(identity, Vader5ProMotorCalibration.ParseCsv("nope"));
            var sample = "120,80,9;100,100,0;95,110,6;88,105,3";
            Assert.Equal(sample, Vader5ProMotorCalibration.ToCsv(
                Vader5ProMotorCalibration.ParseCsv(sample)));
            var bounded = Vader5ProMotorCalibration.ParseCsv(
                "-4,900,200;100,100,0;100,100,0;100,100,0");
            Assert.Equal(0, bounded.GripLeft.GainPercent);
            Assert.Equal(180, bounded.GripLeft.GammaPercent);
            Assert.Equal(64, bounded.GripLeft.MinDrive);
        }

        [Fact]
        public void NewSettings_PersistViaAppSettingsXmlWithSafeDefaults()
        {
            var settings = new PadForge.Services.AppSettingsData();
            Assert.True(settings.FlydigiVader5ProEnhancedHapticsEnabled);
            Assert.False(settings.FlydigiVader5ProSpectralTriggerAssist);
            settings.FlydigiVader5ProSpectralTriggerAssist = true;
            settings.FlydigiVader5ProMotorCalibrationCsv =
                "120,80,9;100,100,0;95,110,6;88,105,3";
            var serializer = new System.Xml.Serialization.XmlSerializer(
                typeof(PadForge.Services.AppSettingsData));
            using var output = new System.IO.StringWriter();
            serializer.Serialize(output, settings);
            using var input = new System.IO.StringReader(output.ToString());
            var restored = (PadForge.Services.AppSettingsData)serializer.Deserialize(input)!;
            Assert.True(restored.FlydigiVader5ProSpectralTriggerAssist);
            Assert.Equal(settings.FlydigiVader5ProMotorCalibrationCsv,
                restored.FlydigiVader5ProMotorCalibrationCsv);
        }
    }
}
