using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// DualShock 4 Bluetooth audio report layout (#528): the speaker and
    /// headset stream, the 0x11 arm report, the input status byte and the
    /// headset microphone frames.
    ///
    /// <para>Byte 2 of every audio report selects the controller's inbound
    /// report mode. 0xA0 keeps ordinary input flowing while the speaker or
    /// headset plays, and 0xA1 adds the headset microphone. The hbashton fork
    /// of DS4Windows records that 0xA2, which DS4AudioStreamer sent and
    /// PadForge copied, stops a genuine CUH-ZCT2's input after a few reports
    /// (DualShock4BluetoothAudioProtocol.cs:202 at 68a5c64). Byte 1 stays
    /// 0x40 (stream) or 0xC0 (control): its low six bits are the input
    /// interval, and PadForge asks for the fastest everywhere, as its effects
    /// reports do.</para>
    ///
    /// <para>Targets, volume ranges and status bits per ds4mac
    /// (docs/09-Audio-Streaming-Feature.md) and ds4drv (device.py). The
    /// microphone framing per the hbashton fork: the audio block follows the
    /// 75-byte controller state when input byte 1 bit 7 is set and starts at
    /// byte 3 when it is clear, then a 16-bit frame number, the target (0x01
    /// on a genuine CUH-ZCT2, 0x03 in older notes) and the frames.</para>
    /// </summary>
    internal static class Ds4BtAudio
    {
        public const byte ModeSpeaker = 0xA0;
        public const byte ModeMicrophone = 0xA1;

        public const byte TargetSpeaker = 0x02;
        public const byte TargetHeadset = 0x24;
        public const byte MicTargetGenuine = 0x01;
        public const byte MicTargetLegacy = 0x03;

        public const int ControlReportSize = 78;
        public const int MaxInputReportSize = 547;

        /// <summary>Headphone and speaker volume ceiling: 0x4F in ds4mac and
        /// in the hbashton fork's ScaleAudioVolume (DS4Device.cs at 68a5c64).
        /// DS4AudioStreamer sends 0x50, and DS4-Sound-Link writes up to 127.</summary>
        public const byte VolumeMax = 0x4F;
        /// <summary>Microphone gain ceiling: 0x40 in ds4mac and in the
        /// hbashton fork's ScaleAudioVolume.</summary>
        public const byte MicGainMax = 0x40;

        // Byte 3 of report 0x11 enables the volume bytes it carries.
        public const byte FlagHeadphoneLeft = 0x10;
        public const byte FlagHeadphoneRight = 0x20;
        public const byte FlagMicrophone = 0x40;
        public const byte FlagSpeaker = 0x80;

        /// <summary>Raw offset of the status byte in a state-bearing Bluetooth
        /// input report: SDL's packet offset 29 after the 3-byte Bluetooth
        /// header, ds4drv's USB offset 30 shifted by the extra header byte.</summary>
        public const int StatusOffset = 32;
        public const byte StatusHeadphones = 0x20;
        public const byte StatusMicrophone = 0x40;

        /// <summary>Offset of the same status byte in a USB input report 0x01,
        /// counting the report ID: ds4drv's parse_report reads buf[30], and
        /// SDL_hidapi_ps4.c's PS4StatePacket_t puts ucBatteryLevel 29 bytes
        /// after the ID. The report carries no CRC.</summary>
        public const int UsbStatusOffset = 30;

        /// <summary>Length of a USB input report 0x01 with the report ID:
        /// ds4drv's HidrawUSBDS4Device reads 64 bytes, and SDL takes a state
        /// packet from 64 bytes up.</summary>
        public const int UsbReportSize = 64;

        /// <summary>Bit 2 of byte 31 of a USB report 0x01 is set while no pad
        /// is linked to Sony's adapter and is always clear on a wired pad
        /// (SDL_hidapi_ps4.c HIDAPI_DriverPS4_IsPacketValid, DS4Windows'
        /// synced check on the same byte).</summary>
        public const byte UsbNoPadLinked = 0x04;

        private const int StateBytes = 75;

        /// <summary>Length of a Bluetooth input report by ID: 0x11 is 78
        /// bytes, each later ID adds 64 up to 0x18 at 526, and 0x19 is 547.
        /// Zero for anything else.</summary>
        public static int InputReportLength(byte reportId) => reportId switch
        {
            0x11 => 78, 0x12 => 142, 0x13 => 206, 0x14 => 270, 0x15 => 334,
            0x16 => 398, 0x17 => 462, 0x18 => 526, 0x19 => 547,
            _ => 0,
        };

        /// <summary>Headphone Volume (0 to 100%) as the report byte, 0x00 to 0x4F.</summary>
        public static byte HeadphoneVolumeByte(int percent)
            => (byte)Math.Round(Math.Clamp(percent, 0, 100) * VolumeMax / 100.0);

        /// <summary>Stream target for a resolved Output Path. Headphones
        /// (Stereo) and Headphones (Mono) go to the jack, everything else to
        /// the speaker: Default and Speaker Only by design, and Headphones +
        /// Speaker because the DualShock 4 has no route that plays both.</summary>
        public static byte TargetFor(int resolvedPath)
            => resolvedPath is 1 or 2 ? TargetHeadset : TargetSpeaker;

        /// <summary>Only Headphones (Stereo) carries stereo. The speaker is
        /// mono and taps one channel of a stereo stream, so its frames fold
        /// to mono, and Headphones (Mono) folds by its own definition.</summary>
        public static bool IsStereoRoute(int resolvedPath) => resolvedPath == 1;

        /// <summary>A route's headphone and speaker volume bytes. The target
        /// byte alone does not isolate the outputs: DS4-Sound-Link's output
        /// switch zeroes the speaker for the jack and the jack for the
        /// speaker (AudioStreamingController.SetOutputDestinationAsync at
        /// 8267b6e), and the hbashton fork zeroes the speaker for
        /// headset-only audio (ControlService.cs:2644 at 68a5c64). So the jack
        /// routes mute the speaker, Speaker Only mutes the jack, and Default
        /// leaves both on, as before #528.</summary>
        public static (byte Headphone, byte Speaker) VolumesFor(int resolvedPath, int headphonePercent)
        {
            byte headphone = HeadphoneVolumeByte(headphonePercent);
            return resolvedPath switch
            {
                1 or 2 => (headphone, (byte)0),
                4 => ((byte)0, VolumeMax),
                _ => (headphone, VolumeMax),
            };
        }

        /// <summary>Writes bytes 0 to 5 of a 0x17 (four frames) or 0x14 (two
        /// frames) stream report.</summary>
        public static void WriteStreamHeader(Span<byte> report, bool fourFrames, ushort frameCounter,
                                             byte mode, byte target)
        {
            report[0] = fourFrames ? (byte)0x17 : (byte)0x14;
            report[1] = 0x40;
            report[2] = mode;
            report[3] = (byte)frameCounter;
            report[4] = (byte)(frameCounter >> 8);
            report[5] = target;
        }

        /// <summary>The one-shot report 0x11 the audio lane sends when it
        /// builds its transport: the mode byte and the volume enables only,
        /// with the route's volumes from <see cref="VolumesFor"/>. Rumble,
        /// lightbar and flash belong to the effects writer, so their enables
        /// stay clear.</summary>
        public static byte[] BuildControlReport(byte mode, byte headphoneVolume, byte speakerVolume, bool microphone)
        {
            var r = new byte[ControlReportSize];
            r[0] = 0x11;
            r[1] = 0xC0;
            r[2] = mode;
            r[3] = (byte)(FlagHeadphoneLeft | FlagHeadphoneRight | FlagSpeaker
                          | (microphone ? FlagMicrophone : 0));
            r[21] = headphoneVolume;
            r[22] = headphoneVolume;
            r[23] = microphone ? MicGainMax : (byte)0;
            r[24] = speakerVolume;
            WriteOutputCrc(r, ControlReportSize);
            return r;
        }

        /// <summary>The voice lane's own report 0x11, for a pad that may have
        /// no effects writer to carry its mode byte. Armed, it is the
        /// keepalive the hbashton fork sends once a second while the
        /// microphone is on (DS4Device.cs:2061-2070 at 68a5c64): byte 2 is
        /// 0xA1 and byte 3 enables only the microphone gain, in byte 23
        /// (WriteAudioControlReport in DualShock4BluetoothAudioProtocol.cs).
        /// Disarmed, byte 2 is 0xA0 and byte 3 enables nothing. Rumble,
        /// lightbar and both volumes keep their enables clear, so the
        /// firmware leaves them to their owners.</summary>
        public static byte[] BuildMicModeReport(bool armed)
        {
            var r = new byte[ControlReportSize];
            r[0] = 0x11;
            r[1] = 0xC0;
            r[2] = armed ? ModeMicrophone : ModeSpeaker;
            if (armed)
            {
                r[3] = FlagMicrophone;
                r[23] = MicGainMax;
            }
            WriteOutputCrc(r, ControlReportSize);
            return r;
        }

        // Byte-at-a-time table for the reflected polynomial 0xEDB88320. The
        // microphone reader checks every input report, up to 547 bytes at the
        // controller's report rate, so a bitwise loop would cost eight times
        // the work on the slowest PCs PadForge supports.
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>CRC-32 over a one-byte Bluetooth transaction prefix (0xA1
        /// input, 0xA2 output) and the report bytes.</summary>
        public static uint Crc32(byte prefix, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            crc = CrcTable[(crc ^ prefix) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        /// <summary>Writes the output CRC into the last four bytes of a report.</summary>
        public static void WriteOutputCrc(Span<byte> report, int size)
        {
            uint crc = Crc32(0xA2, report.Slice(0, size - 4));
            report[size - 4] = (byte)crc;
            report[size - 3] = (byte)(crc >> 8);
            report[size - 2] = (byte)(crc >> 16);
            report[size - 1] = (byte)(crc >> 24);
        }

        /// <summary>True when an input report of a known ID carries a valid
        /// CRC at its own length.</summary>
        public static bool ValidateInputCrc(ReadOnlySpan<byte> report, int got)
        {
            if (got < 1) return false;
            int length = InputReportLength(report[0]);
            if (length == 0 || got < length) return false;
            int at = length - 4;
            uint expected = (uint)(report[at] | report[at + 1] << 8 | report[at + 2] << 16 | report[at + 3] << 24);
            return expected == Crc32(0xA1, report.Slice(0, at));
        }

        /// <summary>Reads the status byte of a valid state-bearing report:
        /// headphones in bit 5, a microphone in bit 6.</summary>
        public static bool TryReadStatus(ReadOnlySpan<byte> report, int got, out byte status)
        {
            status = 0;
            if (!ValidateInputCrc(report, got)) return false;
            if ((report[1] & 0x80) == 0) return false;
            status = report[StatusOffset];
            return true;
        }

        /// <summary>Reads the status byte of a USB input report 0x01:
        /// headphones in bit 5, a microphone in bit 6. A report shorter than
        /// a state report, or one from Sony's adapter with no pad linked,
        /// carries no status, the rule SDL's packet check applies.</summary>
        public static bool TryReadUsbStatus(ReadOnlySpan<byte> report, int got, out byte status)
        {
            status = 0;
            if (got < UsbReportSize || report.Length < UsbReportSize || report[0] != 0x01
                || (report[UsbStatusOffset + 1] & UsbNoPadLinked) != 0) return false;
            status = report[UsbStatusOffset];
            return true;
        }

        public delegate void MicFrameHandler(ushort sequence, ReadOnlySpan<byte> frame);

        /// <summary>Hands each microphone frame of a valid input report to
        /// <paramref name="handler"/> with its own frame number. Returns the
        /// number of frames found, or −1 when the report fails its CRC. The
        /// short report 0x01 has no CRC and no audio, so it returns 0.</summary>
        public static int ExtractMicFrames(ReadOnlySpan<byte> report, int got, MicFrameHandler handler)
        {
            if (got < 1 || InputReportLength(report[0]) == 0) return 0;
            if (!ValidateInputCrc(report, got)) return -1;
            if ((report[2] & 0x80) == 0) return 0; // no audio in this report
            int end = InputReportLength(report[0]) - 4;
            int at = FindMicBlock(report, end);
            if (at < 0) return 0;

            ushort first = (ushort)(report[at] | report[at + 1] << 8);
            int count = 0, scan = at + 3;
            while (scan + 4 <= end)
            {
                int length = 0;
                if (report[scan] == 0xAD) length = SbcDecoder.MsbcFrameLength;
                else if (report[scan] == 0x9C && SbcDecoder.TryReadHeader(report.Slice(scan, 4), out var info))
                    length = info.FrameLength;
                if (length <= 0) { scan++; continue; }
                if (scan + length > end) break;
                handler((ushort)(first + count), report.Slice(scan, length));
                count++;
                scan += length;
            }
            return count;
        }

        // State first when byte 1 bit 7 says so, audio alone otherwise, and
        // the state-prefixed spot again for firmware that leaves the flag
        // stale (the hbashton fork's FindMicrophoneAudioOffset).
        private static int FindMicBlock(ReadOnlySpan<byte> report, int end)
        {
            int afterState = 3 + StateBytes;
            if ((report[1] & 0x80) != 0 && afterState + 3 <= end && IsMicTarget(report[afterState + 2]))
                return afterState;
            if (3 + 3 <= end && IsMicTarget(report[3 + 2]))
                return 3;
            if (afterState + 3 <= end && IsMicTarget(report[afterState + 2]))
                return afterState;
            return -1;
        }

        private static bool IsMicTarget(byte target)
            => target == MicTargetGenuine || target == MicTargetLegacy;
    }

    /// <summary>
    /// One DualShock 4 headset microphone, from SBC or mSBC frames to 16 kHz
    /// mono 16-bit samples. Lost frames, counted by frame number, are
    /// concealed the way the hbashton fork does it
    /// (ViiperOutDevice.cs:2354-2405 at 68a5c64): up to four repeats of the
    /// last decoded frame, each 0.82 times quieter than the one before, then
    /// a 16-sample crossfade into the next real frame. A longer gap restarts
    /// the decoder. The fork accepts only 16 kHz mono, and so does this. The
    /// virtual DualShock 4's own microphone is 16 kHz mono too, so it takes
    /// these samples as they come, and the 48 kHz consumers go through
    /// <see cref="Upsampler3x"/>.
    /// </summary>
    internal sealed class Ds4MicStream
    {
        public const int MaxConcealedFrames = 4;
        private const double ConcealDecay = 0.82;
        private const int CrossfadeSamples = 16;
        public const int SourceRate = 16000;

        private readonly SbcDecoder _decoder = new();
        private readonly short[] _frame = new short[SbcDecoder.MaxSamplesPerChannel * 2];
        private readonly short[] _last = new short[SbcDecoder.MaxSamplesPerChannel];
        private readonly short[] _conceal = new short[SbcDecoder.MaxSamplesPerChannel];
        private int _lastCount;
        private ushort _lastSequence;
        private bool _haveSequence;

        public long FramesDecoded, FramesConcealed, Discontinuities, DecodeFailures, OutOfOrder;

        public delegate void Pcm16kSink(ReadOnlySpan<short> mono16k);

        /// <summary>Forgets the stream, as when the lane restarts.</summary>
        public void Reset()
        {
            _decoder.Reset();
            _lastCount = 0;
            _haveSequence = false;
        }

        public void Feed(ushort sequence, ReadOnlySpan<byte> frame, Pcm16kSink sink)
        {
            int missing = 0;
            if (_haveSequence)
            {
                ushort delta = (ushort)(sequence - _lastSequence);
                if (delta == 0) return;                       // the same frame again
                if (delta >= 0x8000) { OutOfOrder++; return; } // older than the last one
                missing = delta - 1;
                if (missing > MaxConcealedFrames)
                {
                    Discontinuities++;
                    _decoder.Reset();
                    _lastCount = 0;
                    missing = 0;
                }
            }

            int n = _decoder.Decode(frame, _frame, out var info);
            if (n <= 0 || info.Channels != 1 || info.SampleRate != SourceRate)
            {
                DecodeFailures++;
                return;
            }
            FramesDecoded++;

            if (missing > 0)
            {
                for (int m = 0; m < missing; m++)
                {
                    int count = _lastCount > 0 ? _lastCount : n;
                    double gain = Math.Pow(ConcealDecay, m + 1);
                    for (int i = 0; i < count; i++)
                        _conceal[i] = _lastCount > 0
                            ? (short)Math.Clamp(Math.Round(_last[i] * gain), short.MinValue, short.MaxValue)
                            : (short)0;
                    sink(_conceal.AsSpan(0, count));
                    FramesConcealed++;
                }
                if (_lastCount > 0)
                {
                    int count = Math.Min(CrossfadeSamples, Math.Min(n, _lastCount));
                    double gain = Math.Pow(ConcealDecay, missing);
                    int from = _lastCount - count;
                    for (int i = 0; i < count; i++)
                    {
                        double blend = (i + 1.0) / (count + 1.0);
                        double previous = _last[from + i] * gain;
                        _frame[i] = (short)Math.Clamp(Math.Round(previous * (1.0 - blend) + _frame[i] * blend),
                                                      short.MinValue, short.MaxValue);
                    }
                }
            }

            sink(_frame.AsSpan(0, n));
            Array.Copy(_frame, _last, n);
            _lastCount = n;
            _lastSequence = sequence;
            _haveSequence = true;
        }
    }

    /// <summary>
    /// 16 kHz to 48 kHz by three, a polyphase low-pass of 96 taps: a
    /// Blackman-windowed sinc cut off at 7.4 kHz, each phase scaled to unity
    /// DC gain. A 16 kHz stream carries nothing above 8 kHz, so the images
    /// that tripling the rate makes, from 8 kHz up, sit in the stopband.
    /// </summary>
    internal sealed class Upsampler3x
    {
        private const int Factor = 3;
        private const int TapsPerPhase = 32;
        private const double CutoffHz = 7400;
        private const double OutputRate = 48000;

        private static readonly double[][] Phases = BuildPhases();

        private static double[][] BuildPhases()
        {
            int taps = Factor * TapsPerPhase;
            double fc = CutoffHz / OutputRate;
            double center = (taps - 1) / 2.0;
            var h = new double[taps];
            for (int n = 0; n < taps; n++)
            {
                double x = n - center;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (taps - 1))
                         + 0.08 * Math.Cos(4 * Math.PI * n / (taps - 1));
                h[n] = sinc * w;
            }
            var phases = new double[Factor][];
            for (int p = 0; p < Factor; p++)
            {
                phases[p] = new double[TapsPerPhase];
                double sum = 0;
                for (int k = 0; k < TapsPerPhase; k++) { phases[p][k] = h[k * Factor + p]; sum += phases[p][k]; }
                for (int k = 0; k < TapsPerPhase; k++) phases[p][k] /= sum;
            }
            return phases;
        }

        // Input history, newest sample at [0].
        private readonly double[] _history = new double[TapsPerPhase];

        public void Reset() => Array.Clear(_history);

        /// <summary>Writes three output samples per input sample and returns
        /// how many it wrote.</summary>
        public int Process(ReadOnlySpan<short> input, Span<short> output)
        {
            int o = 0;
            foreach (short x in input)
            {
                Array.Copy(_history, 0, _history, 1, TapsPerPhase - 1);
                _history[0] = x;
                for (int p = 0; p < Factor; p++)
                {
                    double[] taps = Phases[p];
                    double y = 0;
                    for (int k = 0; k < TapsPerPhase; k++) y += taps[k] * _history[k];
                    output[o++] = (short)Math.Clamp(Math.Round(y), short.MinValue, short.MaxValue);
                }
            }
            return o;
        }
    }
}
