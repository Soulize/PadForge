using System;
using System.Text.Json;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Conservative, user-adjustable frequency attenuation, not a fabricated
    /// DualSense frequency-response measurement. Unity weights preserve the
    /// PCM's absolute RMS level; weights can only attenuate, never boost.
    /// </summary>
    internal static class Vader5ProFidelitySettings
    {
        internal sealed record BandWeights(int LowPercent, int MidPercent, int HighPercent)
        {
            internal static BandWeights Neutral { get; } = new(100, 100, 100);
        }

        private static BandWeights _bands = BandWeights.Neutral;
        private static int _transientEnhancement;

        internal static BandWeights CurrentBands => Volatile.Read(ref _bands);
        internal static bool TransientEnhancementEnabled => Volatile.Read(ref _transientEnhancement) != 0;
        internal static void SetTransientEnhancement(bool enabled)
            => Volatile.Write(ref _transientEnhancement, enabled ? 1 : 0);

        internal static void SetBands(BandWeights value)
        {
            value ??= BandWeights.Neutral;
            Volatile.Write(ref _bands, new BandWeights(
                Math.Clamp(value.LowPercent, 0, 100),
                Math.Clamp(value.MidPercent, 0, 100),
                Math.Clamp(value.HighPercent, 0, 100)));
        }

        internal static string ToCsv(BandWeights bands)
        {
            bands ??= BandWeights.Neutral;
            return $"{bands.LowPercent},{bands.MidPercent},{bands.HighPercent}";
        }

        internal static BandWeights ParseCsv(string csv)
        {
            var parts = (csv ?? "").Split(',');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int low)
                || !int.TryParse(parts[1], out int mid)
                || !int.TryParse(parts[2], out int high))
                return BandWeights.Neutral;
            return new BandWeights(Math.Clamp(low, 0, 100),
                Math.Clamp(mid, 0, 100), Math.Clamp(high, 0, 100));
        }

        /// <summary>
        /// Energy-ratio weighting is bounded to [0, raw RMS]. Sum of band
        /// energies is normalized away, so overlapping IIR transition bands
        /// never create an artificial loudness boost even on fresh state.
        /// </summary>
        internal static double PerceptualRms(double rawSquare, double lowSquare,
            double midSquare, double highSquare, BandWeights weights)
        {
            if (rawSquare <= 0) return 0;
            weights ??= BandWeights.Neutral;
            double sum = lowSquare + midSquare + highSquare;
            if (sum <= 1e-16) return Math.Sqrt(rawSquare);
            double low = Math.Clamp(weights.LowPercent, 0, 100) / 100.0;
            double mid = Math.Clamp(weights.MidPercent, 0, 100) / 100.0;
            double high = Math.Clamp(weights.HighPercent, 0, 100) / 100.0;
            double energy = lowSquare * low * low + midSquare * mid * mid
                + highSquare * high * high;
            return Math.Sqrt(rawSquare) * Math.Sqrt(
                Math.Clamp(energy / sum, 0.0, 1.0));
        }
    }

    /// <summary>
    /// Four independent, monotone nine-knot compensation tables. Input is
    /// desired relative level; output is the 8-bit native HID motor command.
    /// Identity by default: no physical actuator measurements are assumed.
    /// The zero knot must remain zero to guarantee immediate stop intent.
    /// </summary>
    internal static class Vader5ProMotorResponseLut
    {
        internal const int KnotCount = 9;
        internal sealed record Curve(byte P0, byte P1, byte P2, byte P3, byte P4,
            byte P5, byte P6, byte P7, byte P8)
        {
            internal static Curve Identity { get; } =
                new(0, 32, 64, 96, 128, 159, 191, 223, 255);

            internal byte At(int index) => index switch
            {
                0 => P0, 1 => P1, 2 => P2, 3 => P3, 4 => P4,
                5 => P5, 6 => P6, 7 => P7, _ => P8
            };

            internal byte Map(byte input)
            {
                if (input == 0) return 0;
                // 8 equal intervals over the full 0..255 requested range.
                int scaled = input * 8;
                int i = scaled / 255;
                if (i >= 8) return P8;
                int remainder = scaled - i * 255;
                int a = At(i), b = At(i + 1);
                return (byte)Math.Clamp((a * (255 - remainder)
                    + b * remainder + 127) / 255, 0, 255);
            }

            internal string ToCsv() => string.Join(",",
                P0, P1, P2, P3, P4, P5, P6, P7, P8);

            internal int[] ToArray() => new int[]
                { P0, P1, P2, P3, P4, P5, P6, P7, P8 };

            internal static bool TryCreate(int[] values, out Curve curve)
            {
                curve = null;
                if (values == null || values.Length != KnotCount
                    || values[0] != 0) return false;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] < 0 || values[i] > 255
                        || (i > 0 && values[i] < values[i - 1]))
                        return false;
                }
                curve = new Curve((byte)values[0], (byte)values[1], (byte)values[2],
                    (byte)values[3], (byte)values[4], (byte)values[5],
                    (byte)values[6], (byte)values[7], (byte)values[8]);
                return true;
            }

            internal static bool TryParse(string csv, out Curve curve)
            {
                curve = null;
                var parts = (csv ?? "").Split(',');
                if (parts.Length != KnotCount) return false;
                var points = new int[KnotCount];
                for (int i = 0; i < points.Length; i++)
                    if (!int.TryParse(parts[i], out points[i])) return false;
                return TryCreate(points, out curve);
            }
        }

        internal sealed record Profile(Curve GripLeft, Curve GripRight,
            Curve TriggerLeft, Curve TriggerRight)
        {
            internal static Profile Identity { get; } = new(
                Curve.Identity, Curve.Identity, Curve.Identity, Curve.Identity);
        }

        private static Profile _current = Profile.Identity;
        internal static Profile Current => Volatile.Read(ref _current);
        internal static void SetProfile(Profile value)
            => Volatile.Write(ref _current, value ?? Profile.Identity);

        internal static byte Map(byte requested, Curve curve)
            => (curve ?? Curve.Identity).Map(requested);

        internal static string ToCsv(Profile value)
        {
            value ??= Profile.Identity;
            return string.Join(";", value.GripLeft.ToCsv(), value.GripRight.ToCsv(),
                value.TriggerLeft.ToCsv(), value.TriggerRight.ToCsv());
        }

        internal static Profile ParseCsv(string value)
            => TryParseCsv(value, out var profile) ? profile : Profile.Identity;

        internal static bool TryParseCsv(string value, out Profile profile)
        {
            profile = null;
            var parts = (value ?? "").Split(';');
            if (parts.Length != 4
                || !Curve.TryParse(parts[0], out var gl)
                || !Curve.TryParse(parts[1], out var gr)
                || !Curve.TryParse(parts[2], out var tl)
                || !Curve.TryParse(parts[3], out var tr))
                return false;
            profile = new Profile(gl, gr, tl, tr);
            return true;
        }

        internal static FlydigiVader5ProHidWriter.Motors Apply(
            Vader5ProMotorCalibration.Motors calibrated, Profile profile)
        {
            profile ??= Profile.Identity;
            return new FlydigiVader5ProHidWriter.Motors(
                profile.GripLeft.Map(calibrated.GripLeft),
                profile.GripRight.Map(calibrated.GripRight),
                profile.TriggerLeft.Map(calibrated.TriggerLeft),
                profile.TriggerRight.Map(calibrated.TriggerRight));
        }
    }

    /// <summary>
    /// Portable, versioned, validated actuator-response presets. A file is
    /// user-supplied calibration data, NOT proof of measured DualSense parity.
    /// No runtime state is changed by a failed import.
    /// </summary>
    internal static class Vader5ProFidelityCalibrationFile
    {
        internal const string Format = "padforge-vader5pro-fidelity";
        internal const int Version = 1;

        internal sealed class Document
        {
            public string Format { get; set; }
            public int Version { get; set; }
            public int[][] MotorLuts { get; set; }
            public int[] BandWeights { get; set; }
            public int[][] GainGammaFloor { get; set; }
            public string CalibrationNotes { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        internal static string Export(Vader5ProMotorResponseLut.Profile luts,
            Vader5ProFidelitySettings.BandWeights weights,
            Vader5ProMotorCalibration.Profile gains)
        {
            luts ??= Vader5ProMotorResponseLut.Profile.Identity;
            weights ??= Vader5ProFidelitySettings.BandWeights.Neutral;
            gains ??= Vader5ProMotorCalibration.Profile.Identity;
            static int[] G(Vader5ProMotorCalibration.Curve c)
                => new[] { c.GainPercent, c.GammaPercent, c.MinDrive };
            var dto = new Document
            {
                Format = Format,
                Version = Version,
                MotorLuts = new[]
                {
                    luts.GripLeft.ToArray(), luts.GripRight.ToArray(),
                    luts.TriggerLeft.ToArray(), luts.TriggerRight.ToArray()
                },
                BandWeights = new[]
                    { weights.LowPercent, weights.MidPercent, weights.HighPercent },
                GainGammaFloor = new[]
                {
                    G(gains.GripLeft), G(gains.GripRight),
                    G(gains.TriggerLeft), G(gains.TriggerRight)
                },
                CalibrationNotes = "Unmeasured default unless calibrated with physical vibration measurements."
            };
            return JsonSerializer.Serialize(dto, JsonOptions);
        }

        internal static bool TryImport(string json,
            out Vader5ProMotorResponseLut.Profile luts,
            out Vader5ProFidelitySettings.BandWeights weights,
            out Vader5ProMotorCalibration.Profile gains, out string error)
        {
            luts = null; weights = null; gains = null;
            error = "Invalid or unsupported Vader 5 Pro calibration JSON.";
            Document dto;
            try { dto = JsonSerializer.Deserialize<Document>(json, JsonOptions); }
            catch (JsonException) { return false; }
            catch (ArgumentException) { return false; }

            if (dto?.Format != Format || dto.Version != Version
                || dto.MotorLuts?.Length != 4
                || dto.BandWeights?.Length != 3
                || dto.GainGammaFloor?.Length != 4)
                return false;

            var curves = new Vader5ProMotorResponseLut.Curve[4];
            var levels = new Vader5ProMotorCalibration.Curve[4];
            for (int i = 0; i < 4; i++)
            {
                if (!Vader5ProMotorResponseLut.Curve.TryCreate(dto.MotorLuts[i], out curves[i]))
                    return false;
                var g = dto.GainGammaFloor[i];
                if (g == null || g.Length != 3
                    || g[0] < 0 || g[0] > 200
                    || g[1] < 50 || g[1] > 180
                    || g[2] < 0 || g[2] > 64)
                    return false;
                levels[i] = new Vader5ProMotorCalibration.Curve(g[0], g[1], g[2]);
            }
            foreach (int weight in dto.BandWeights)
                if (weight < 0 || weight > 100) return false;

            luts = new(curves[0], curves[1], curves[2], curves[3]);
            weights = new(dto.BandWeights[0], dto.BandWeights[1], dto.BandWeights[2]);
            gains = new(levels[0], levels[1], levels[2], levels[3]);
            error = null;
            return true;
        }
    }
}
