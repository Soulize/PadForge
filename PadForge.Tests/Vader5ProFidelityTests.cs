using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Fidelity regression: source amplitude and time remain source
    /// authored. Distortion is only allowed in explicit opt-in effects or a
    /// user-supplied physical actuator compensation curve.</summary>
    public class Vader5ProFidelityTests
    {
        private static byte[] Tone(int rate, int durationMs, double hz,
            double left = 0.5, double right = 0)
        {
            int frames = rate * durationMs / 1000;
            var bytes = new byte[frames * 4];
            for (int i = 0; i < frames; ++i)
            {
                short l = (short)(Math.Sin(i * 2 * Math.PI * hz / rate) * left * 32767);
                short r = (short)(Math.Sin(i * 2 * Math.PI * hz / rate) * right * 32767);
                int off = i * 4;
                bytes[off] = (byte)l; bytes[off + 1] = (byte)(l >> 8);
                bytes[off + 2] = (byte)r; bytes[off + 3] = (byte)(r >> 8);
            }
            return bytes;
        }

        private static byte[] InFrame(int rate, int totalMs,
            int signalMs, double frequency, double strength)
        {
            var bytes = new byte[rate * totalMs / 1000 * 4];
            var pulse = Tone(rate, signalMs, frequency, strength);
            Array.Copy(pulse, bytes, pulse.Length);
            return bytes;
        }

        [Theory]
        [InlineData(0.04)]
        [InlineData(0.2)]
        [InlineData(0.4)]
        [InlineData(0.8)]
        public void NeutralFidelity_CopiesSustainedPcmRmsNotOnePointFourTimes(double amplitude)
        {
            var c = new Vader5ProHapticConverter();
            var f = c.Process(Tone(48000, 10, 200, amplitude),
                4, 0, 2, 48000, 10,
                Vader5ProFidelitySettings.BandWeights.Neutral,
                transientEnhancement: false);
            double expected = amplitude / Math.Sqrt(2) * 65535;
            Assert.InRange(f.Left, (ushort)(expected * .97), (ushort)(expected * 1.03));
            Assert.Equal((byte)0, f.PulseLeftMs);
            Assert.Equal((ushort)0, f.Right);
        }

        [Theory]
        [InlineData(0.05)]
        [InlineData(0.3)]
        [InlineData(0.8)]
        public void CallbackContainedCue_IsProportionalAndMatchesSourceDuration(double amplitude)
        {
            var c = new Vader5ProHapticConverter();
            var f = c.Process(InFrame(48000, 12, 3, 500, amplitude),
                4, 0, 2, 48000, 12,
                Vader5ProFidelitySettings.BandWeights.Neutral,
                transientEnhancement: false);
            double expected = amplitude * Math.Sqrt(3.0 / 12.0) / Math.Sqrt(2.0) * 65535;
            Assert.InRange(f.Left, (ushort)(expected * .93), (ushort)(expected * 1.07));
            Assert.Equal(3, f.PulseLeftMs);
            Assert.Equal((byte)0, f.PulseRightMs);
        }

        [Fact]
        public void OptionalImpactEmphasis_IsDisabledByDefault_AndBoostsOnlyWhenAsked()
        {
            var raw = InFrame(48000, 12, 3, 500, .8);
            var reference = new Vader5ProHapticConverter().Process(
                raw, 4, 0, 2, 48000, 12,
                Vader5ProFidelitySettings.BandWeights.Neutral, false);
            var boosted = new Vader5ProHapticConverter().Process(
                raw, 4, 0, 2, 48000, 12,
                Vader5ProFidelitySettings.BandWeights.Neutral, true);
            Assert.True(boosted.Left > reference.Left * 1.3);
            Assert.Equal(reference.PulseLeftMs, boosted.PulseLeftMs);
            Assert.False(new PadForge.Services.AppSettingsData()
                .FlydigiVader5ProTransientEnhancement);
        }

        [Fact]
        public void ContinuousSine_NoFabricatedPulseAndZeroCrossingsSafe()
        {
            var c = new Vader5ProHapticConverter();
            for (int i = 0; i < 24; i++)
            {
                var f = c.Process(Tone(48000, 10, 80, .06, .02),
                    4, 0, 2, 48000, i * 10L);
                Assert.True(f.Left > f.Right && f.Right > 0);
                Assert.Equal((byte)0, f.PulseLeftMs);
                Assert.Equal((byte)0, f.PulseRightMs);
            }
        }

        [Fact]
        public void ShortPulseIsNotExtendedToOldSixOrTenMilliseconds()
        {
            var c = new Vader5ProHapticConverter();
            var f = c.Process(InFrame(48000, 12, 4, 500, .4),
                4, 0, 2, 48000, 12);
            Assert.Equal(4, f.PulseLeftMs);
            var silent = c.Process(new byte[48000 * 5 / 1000 * 4],
                4, 0, 2, 48000, 17);
            Assert.Equal((ushort)0, silent.Left);
            Assert.Equal((byte)0, silent.PulseLeftMs);
        }

        [Fact]
        public void FrequencyWeights_AffectGripOutputButNeverAmplifyRawLevel()
        {
            var neutral = Vader5ProFidelitySettings.BandWeights.Neutral;
            var attenuateHigh = new Vader5ProFidelitySettings.BandWeights(100, 100, 25);
            var low = Tone(48000, 10, 65, .4);
            var high = Tone(48000, 10, 1200, .4);
            var baselineLow = new Vader5ProHapticConverter().Process(
                low, 4, 0, 2, 48000, 10, neutral, false);
            var tunedLow = new Vader5ProHapticConverter().Process(
                low, 4, 0, 2, 48000, 10, attenuateHigh, false);
            var baselineHigh = new Vader5ProHapticConverter().Process(
                high, 4, 0, 2, 48000, 10, neutral, false);
            var tunedHigh = new Vader5ProHapticConverter().Process(
                high, 4, 0, 2, 48000, 10, attenuateHigh, false);
            Assert.True(tunedHigh.Left < baselineHigh.Left * .55,
                "High-frequency attenuation should affect grip command.");
            Assert.True(tunedLow.Left > tunedHigh.Left,
                "Frequency weighting should not flatten distinct tones.");
            Assert.True(tunedLow.Left <= baselineLow.Left);
            Assert.True(tunedHigh.Left <= baselineHigh.Left);
        }

        [Fact]
        public void FrequencyWeights_NeutralNeverGainUpForAnyBandEnergy()
        {
            var neutral = Vader5ProFidelitySettings.BandWeights.Neutral;
            Assert.Equal(0, Vader5ProFidelitySettings.PerceptualRms(0, 1, 1, 1, neutral));
            for (int i = 1; i <= 10; ++i)
            {
                double e = i / 10.0;
                Assert.Equal(Math.Sqrt(e),
                    Vader5ProFidelitySettings.PerceptualRms(e, 2 * e, e, .5 * e, neutral),
                    precision: 10);
            }
            Assert.Equal("100,100,100", Vader5ProFidelitySettings.ToCsv(neutral));
            Assert.Equal(neutral, Vader5ProFidelitySettings.ParseCsv("bad"));
        }

        [Fact]
        public void ResponseLut_IdentityMapsByteToItselfWithinOneQuantum()
        {
            var c = Vader5ProMotorResponseLut.Curve.Identity;
            for (int i = 0; i <= 255; ++i)
                Assert.InRange(Math.Abs(c.Map((byte)i) - i), 0, 1);
        }

        [Fact]
        public void ResponseLut_RejectsDescendingPointsAndNeverTurnsZeroIntoMotion()
        {
            Assert.False(Vader5ProMotorResponseLut.Curve.TryParse(
                "0,32,64,96,80,159,191,223,255", out _));
            Assert.False(Vader5ProMotorResponseLut.Curve.TryParse(
                "1,32,64,96,128,159,191,223,255", out _));
            Assert.False(Vader5ProMotorResponseLut.Curve.TryParse(
                "0,32,64,96,128,159,191,223,256", out _));
            Assert.False(Vader5ProMotorResponseLut.Curve.TryParse(
                "0,32,64", out _));
            Assert.True(Vader5ProMotorResponseLut.Curve.TryParse(
                "0,64,112,144,168,192,213,235,255", out var c));
            Assert.Equal((byte)0, c.Map(0));
            byte old = 0;
            for (int i = 0; i <= 255; i++)
            {
                var next = c.Map((byte)i);
                Assert.True(next >= old);
                old = next;
            }
        }

        [Fact]
        public void FourMotorCompensation_Independent_AndAppliedAfterGainGamma()
        {
            var p = new Vader5ProMotorResponseLut.Profile(
                Vader5ProMotorResponseLut.Curve.TryParse(
                    "0,64,112,144,168,192,213,235,255", out var amplified)
                    ? amplified : throw new Exception("test LUT invalid"),
                Vader5ProMotorResponseLut.Curve.Identity,
                Vader5ProMotorResponseLut.Curve.Identity,
                Vader5ProMotorResponseLut.Curve.Identity);
            var linear = new Vader5ProMotorCalibration.Curve(100, 100, 0);
            var pre = Vader5ProMotorCalibration.Apply(32768, 32768,
                32768, 32768,
                new Vader5ProMotorCalibration.Profile(linear, linear, linear, linear));
            var post = Vader5ProMotorResponseLut.Apply(pre, p);
            Assert.True(post.GripLeft > post.GripRight);
            Assert.Equal(post.GripRight, post.TriggerLeft);
            Assert.Equal(post.TriggerLeft, post.TriggerRight);
            Assert.Equal((byte)0,
                Vader5ProMotorResponseLut.Map(0, p.GripLeft));
        }

        [Fact]
        public void CalibrationFile_RoundTrips_Lut_Bands_AndLegacyCurves()
        {
            var luts = Vader5ProMotorResponseLut.Profile.Identity with
            {
                GripLeft = Vader5ProMotorResponseLut.Curve.TryParse(
                    "0,64,112,144,168,192,213,235,255", out var amplified)
                    ? amplified : throw new Exception("test LUT invalid")
            };
            var bands = new Vader5ProFidelitySettings.BandWeights(90, 100, 65);
            var gain = Vader5ProMotorCalibration.Profile.Identity with
            {
                TriggerRight = new Vader5ProMotorCalibration.Curve(110, 100, 0)
            };
            string json = Vader5ProFidelityCalibrationFile.Export(luts, bands, gain);
            Assert.Contains("padforge-vader5pro-fidelity", json);
            Assert.True(Vader5ProFidelityCalibrationFile.TryImport(json,
                out var copyLuts, out var copyBands, out var copyGain, out string error), error);
            Assert.Equal(luts, copyLuts);
            Assert.Equal(bands, copyBands);
            Assert.Equal(gain, copyGain);
        }

        [Fact]
        public void CalibrationFile_RejectsUnsupportedVersionAndMalformedLutAtomically()
        {
            var luts = Vader5ProMotorResponseLut.Profile.Identity;
            var bands = Vader5ProFidelitySettings.BandWeights.Neutral;
            var gains = Vader5ProMotorCalibration.Profile.Identity;
            string json = Vader5ProFidelityCalibrationFile.Export(luts, bands, gains);
            string unsupported = json.Replace("\"version\": 1", "\"version\": 5");
            Assert.False(Vader5ProFidelityCalibrationFile.TryImport(
                unsupported, out var badLut, out var badBand, out var badGain, out _));
            Assert.Null(badLut); Assert.Null(badBand); Assert.Null(badGain);
            // JSON pretty printing uses platform-specific newlines, so do
            // not fabricate invalid data with a fragile text replacement.
            // Mutate the parsed first knot and ensure the importer rejects
            // an actually-invalid nonzero zero-intent motor command.
            var invalid = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            invalid["motorLuts"]![0]![0] = 10;
            string descending = invalid.ToJsonString();
            Assert.False(Vader5ProFidelityCalibrationFile.TryImport(
                descending, out _, out _, out _, out _));
            Assert.False(Vader5ProFidelityCalibrationFile.TryImport(
                "{}", out _, out _, out _, out _));
        }

        [Fact]
        public void SeparatedTinyTextures_DoNotFillTheSilentGapWithMotorDrive()
        {
            var pcm = new byte[48000 * 20 / 1000 * 4];
            var a = Tone(48000, 2, 500, 0.25);
            Array.Copy(a, 0, pcm, 0, a.Length);
            Array.Copy(a, 0, pcm, 48000 * 10 / 1000 * 4, a.Length);
            var f = new Vader5ProHapticConverter().Process(
                pcm, 4, 0, 2, 48000, 20);
            Assert.InRange(f.PulseLeftMs, (byte)3, (byte)5);
            Assert.True(f.PulseLeftMs < 10,
                "Two short textures must not be stretched over their silent gap.");
        }

        [Fact]
        public void SteppedPhysicalMotorTest_PreservesRequestedInputLevel()
        {
            var test = FlydigiVader5ProHidWriter.TestPattern(2, 32);
            Assert.Equal((byte)32, test.TriggerLeft);
            Assert.Equal((byte)0, test.GripLeft);
            Assert.Equal((byte)0, test.GripRight);
            Assert.Equal((byte)0, test.TriggerRight);
        }

        [Fact]
        public void XmlSettings_PersistFidelityAndFourLutsAcrossRestart()
        {
            var s = new PadForge.Services.AppSettingsData
            {
                FlydigiVader5ProEnhancedHapticsEnabled = true,
                FlydigiVader5ProTransientEnhancement = false,
                FlydigiVader5ProBandWeightsCsv = "100,90,45",
                FlydigiVader5ProMotorLutCsv =
                    Vader5ProMotorResponseLut.ToCsv(
                        Vader5ProMotorResponseLut.Profile.Identity),
                FlydigiVader5ProTestStrength = 32
            };
            var serializer = new System.Xml.Serialization.XmlSerializer(
                typeof(PadForge.Services.AppSettingsData));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, s);
            using var reader = new System.IO.StringReader(writer.ToString());
            var restored = (PadForge.Services.AppSettingsData)serializer.Deserialize(reader)!;
            Assert.True(restored.FlydigiVader5ProEnhancedHapticsEnabled);
            Assert.False(restored.FlydigiVader5ProTransientEnhancement);
            Assert.Equal(s.FlydigiVader5ProBandWeightsCsv,
                restored.FlydigiVader5ProBandWeightsCsv);
            Assert.Equal(s.FlydigiVader5ProMotorLutCsv,
                restored.FlydigiVader5ProMotorLutCsv);
            Assert.Equal(32, restored.FlydigiVader5ProTestStrength);
        }
    }
}
