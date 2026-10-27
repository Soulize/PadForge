using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>One rumble state on the wire from both DualSense writers.
    /// The pass-through lane forwards a game's effect frames to the real pad,
    /// and the dispatcher's 30 Hz pass writes the same pad. A frame whose
    /// valid_flag0 bit 1 (haptics select) is clear puts the pad back on audio
    /// haptics (SDL3 SDL_hidapi_ps5.c HIDAPI_DriverPS5_UpdateEffects:
    /// "Leaving emulated rumble bits off will restore audio haptics"), so
    /// PadForge's own rumble on a pass-through target was canceled by every
    /// frame a game playing audio haptics forwarded. The pass now publishes
    /// the rumble it asserts, and the lane writes it into each forwarded frame
    /// that does not select rumble itself. A frame that does is the game
    /// driving rumble and goes out as written.</summary>
    public class PassthroughRumbleClaimTests
    {
        private const byte Legacy = 0x01;          // valid_flag0 bit 0
        private const byte HapticsSelect = 0x02;   // valid_flag0 bit 1
        private const byte Improved = 0x04;        // valid_flag2 bit 2

        private static long PadForgeClaim(byte right, byte left)
            => DualSensePassthroughDispatcher.RumbleClaimFor(
                new Ds5EffectSynthesizer.RumbleState(HapticsSelect | Legacy, Improved, right, left, External: false));

        /// <summary>A game frame on audio haptics: both trigger blocks
        /// flagged, rumble bits clear, motor bytes unflagged.</summary>
        private static byte[] AudioHapticsFrame(int length = 47)
        {
            var p = new byte[length];
            p[0] = 0x0C;
            p[2] = 7;
            p[3] = 9;
            p[10] = 0x26;
            p[21] = 0x26;
            return p;
        }

        // ── The injection ──

        [Fact]
        public void AFrameWithoutHapticsSelect_TakesTheClaimsBitsAndMotors()
        {
            var p = AudioHapticsFrame();
            Assert.True(DualSensePassthroughDispatcher.InjectRumbleClaim(p, PadForgeClaim(200, 100)));
            Assert.Equal(0x0C | HapticsSelect | Legacy, p[0]);
            Assert.Equal(Improved, p[38]);
            Assert.Equal(200, p[2]);   // right motor, DS5EffectsState_t ucRumbleRight
            Assert.Equal(100, p[3]);   // left motor, ucRumbleLeft
            Assert.Equal(0x26, p[10]);
            Assert.Equal(0x26, p[21]);
        }

        [Fact]
        public void AFrameThatSelectsRumble_IsLeftAsTheGameWroteIt()
        {
            var p = new byte[47];
            p[0] = HapticsSelect | Legacy;
            p[2] = 50;
            p[3] = 60;
            var before = (byte[])p.Clone();
            Assert.False(DualSensePassthroughDispatcher.InjectRumbleClaim(p, PadForgeClaim(200, 100)));
            Assert.Equal(before, p);
        }

        [Fact]
        public void APayloadShorterThan39Bytes_TakesNoByte38Write()
        {
            var shortFrame = AudioHapticsFrame(38);
            Assert.True(DualSensePassthroughDispatcher.InjectRumbleClaim(shortFrame, PadForgeClaim(200, 100)));
            Assert.Equal(0x0C | HapticsSelect | Legacy, shortFrame[0]);
            Assert.Equal(200, shortFrame[2]);

            // Positive control: one byte longer reaches valid_flag2.
            var reaching = AudioHapticsFrame(39);
            Assert.True(DualSensePassthroughDispatcher.InjectRumbleClaim(reaching, PadForgeClaim(200, 100)));
            Assert.Equal(Improved, reaching[38]);
        }

        [Fact]
        public void NoClaim_LeavesTheFrameByteForByte()
        {
            var p = AudioHapticsFrame();
            var before = (byte[])p.Clone();
            Assert.False(DualSensePassthroughDispatcher.InjectRumbleClaim(p, 0));
            Assert.Equal(before, p);
            Assert.Equal(DualSensePassthroughDispatcher.BuildEffectReport(false, before),
                DualSensePassthroughDispatcher.BuildEffectReport(false, before, 0));
            Assert.Equal(DualSensePassthroughDispatcher.BuildEffectReport(true, before),
                DualSensePassthroughDispatcher.BuildEffectReport(true, before, 0));
        }

        [Fact]
        public void TheReportCarriesTheClaimAndTheGamesPayloadStaysARepeat()
        {
            var game = AudioHapticsFrame();
            var lastSent = (byte[])game.Clone();   // what the lane recorded for this frame
            long claim = PadForgeClaim(200, 100);

            var usb = DualSensePassthroughDispatcher.BuildEffectReport(false, game, claim);
            Assert.Equal(0x0C | HapticsSelect | Legacy, usb[1]);
            Assert.Equal(200, usb[3]);
            Assert.Equal(100, usb[4]);
            Assert.Equal(Improved, usb[39]);

            // The game's own bytes are what repeat detection and the merge
            // see, so the next identical game frame is still a repeat.
            Assert.Equal(lastSent, game);
            Assert.True(DualSensePassthroughDispatcher.IsRepeatOfLastSent(
                AudioHapticsFrame(), lastSent, lastSent.Length));
        }

        [Fact]
        public void TheBluetoothCrcCoversTheInjectedBytes()
        {
            var bt = DualSensePassthroughDispatcher.BuildEffectReport(true, AudioHapticsFrame(), PadForgeClaim(200, 100));
            Assert.Equal(0x0C | HapticsSelect | Legacy, bt[3]);
            Assert.Equal(200, bt[5]);
            Assert.Equal(100, bt[6]);
            Assert.Equal(Improved, bt[41]);
            var restamped = (byte[])bt.Clone();
            PlayStationEffectWriter.StampSonyBtOutputCrc(restamped);
            Assert.Equal(restamped, bt);
        }

        // ── What the pass claims ──

        [Fact]
        public void OnlyPadForgesOwnRumbleIsClaimed()
        {
            Assert.NotEqual(0L, PadForgeClaim(200, 100));
            // Mirroring a game's rumble claims nothing.
            Assert.Equal(0L, DualSensePassthroughDispatcher.RumbleClaimFor(
                new Ds5EffectSynthesizer.RumbleState(HapticsSelect | Legacy, Improved, 200, 100, External: true)));
            // An idle frame asserts no rumble bit.
            Assert.Equal(0L, DualSensePassthroughDispatcher.RumbleClaimFor(
                new Ds5EffectSynthesizer.RumbleState(0, 0, 200, 100, External: false)));
            // The stop frame asserts the bits with zero motors. The game's own
            // frame without bit 1 already stops emulated rumble.
            Assert.Equal(0L, PadForgeClaim(0, 0));
        }

        [Fact]
        public void AClaimLapsesAfterItsFreshWindow()
        {
            long claim = PadForgeClaim(200, 100);
            long now = 5_000;
            long packed = claim | ((long)(uint)now << 32);
            Assert.Equal(claim, DualSensePassthroughDispatcher.FreshRumbleClaim(packed, now + DualSensePassthroughDispatcher.RumbleClaimFreshMs));
            Assert.Equal(0L, DualSensePassthroughDispatcher.FreshRumbleClaim(packed, now + DualSensePassthroughDispatcher.RumbleClaimFreshMs + 1));

            // Across the 32-bit stamp's wrap.
            long nearWrap = 0xFFFF_FFF0L;
            long wrapped = claim | ((long)(uint)nearWrap << 32);
            Assert.Equal(claim, DualSensePassthroughDispatcher.FreshRumbleClaim(wrapped, nearWrap + 50));
            Assert.Equal(0L, DualSensePassthroughDispatcher.FreshRumbleClaim(wrapped, nearWrap + 500));
        }

        [Fact]
        public void APublishedClaimIsReadBackAndReplacedByNone()
        {
            var device = Guid.NewGuid();
            long now = Environment.TickCount64;
            DualSensePassthroughDispatcher.PublishRumbleClaim(device,
                new Ds5EffectSynthesizer.RumbleState(HapticsSelect | Legacy, Improved, 200, 100, External: false), now);
            Assert.Equal(PadForgeClaim(200, 100), DualSensePassthroughDispatcher.CurrentRumbleClaim(device, now));
            DualSensePassthroughDispatcher.PublishRumbleClaim(device,
                new Ds5EffectSynthesizer.RumbleState(0, 0, 0, 0, External: false), now);
            Assert.Equal(0L, DualSensePassthroughDispatcher.CurrentRumbleClaim(device, now));
        }

        /// <summary>The shutdown release is the lane's last write and hands
        /// the pad back. A claim still fresh when PadForge closes must not
        /// ride it, or PadForge's rumble stays on the pad after it exits,
        /// where the release's clear bit 1 used to stop it.</summary>
        [Fact]
        public void TheShutdownReleaseCarriesNoClaim()
        {
            var device = Guid.NewGuid();
            long now = Environment.TickCount64;
            DualSensePassthroughDispatcher.PublishRumbleClaim(device,
                new Ds5EffectSynthesizer.RumbleState(HapticsSelect | Legacy, Improved, 200, 100, External: false), now);
            var lane = new DualSensePassthroughDispatcher(14);
            try
            {
                Assert.Equal(PadForgeClaim(200, 100), lane.RumbleClaimForWrite(device, now));
            }
            finally { lane.Dispose(); }
            Assert.Equal(0L, lane.RumbleClaimForWrite(device, now));
        }

        /// <summary>The claim is the rumble half of the frame the synthesizer
        /// builds from the same arguments, for every ownership case.</summary>
        [Theory]
        [InlineData(200, 100, true, false)]
        [InlineData(200, 100, false, false)]
        [InlineData(0, 0, true, false)]
        [InlineData(200, 100, true, true)]
        public void ResolveRumble_IsTheSynthesizedFramesRumbleHalf(byte right, byte left, bool assert, bool mirrored)
        {
            var ov = mirrored
                ? new UserEffectsDispatcher.ExternalSubsystemOverrides
                {
                    RumbleRight = 30, RumbleLeft = 40, RumbleFlag0Bits = Legacy, RumbleFlag2Bits = 0,
                }
                : default;
            var f = Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig(),
                rumbleRight: right, rumbleLeft: left, assertRumbleEnable: assert, overrides: ov);
            var r = Ds5EffectSynthesizer.ResolveRumble(right, left, assert, ov);
            Assert.Equal((byte)f["validFlag0"] & 0x03, r.Flag0Bits);
            Assert.Equal((byte)f["validFlag2"] & Improved, r.Flag2Bits);
            Assert.Equal((byte)f["rightMotor"], r.Right);
            Assert.Equal((byte)f["leftMotor"], r.Left);
            Assert.Equal(mirrored, r.External);
        }
    }

    /// <summary>The persona haptics alone, as one device takes them: the
    /// rumble a released, route-down DualSense gets during the mirror's
    /// release grace (report-G G7). Every other PadForge source keeps the
    /// grace, so none of them may reach these bytes.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PersonaReleaseRumbleTests : IDisposable
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly InputManager _im = new();
        private readonly List<Guid> _rings = new();

        public PersonaReleaseRumbleTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            var rings = (System.Collections.Concurrent.ConcurrentDictionary<Guid, AudioPassthroughService.PersonaHapticRing>)
                typeof(AudioPassthroughService).GetField("_personaHapticRings", StaticPrivate)!.GetValue(null)!;
            foreach (var g in _rings) rings.TryRemove(g, out _);
        }

        private Guid AddDualSense(int slot, PadSetting ps)
        {
            var id = Guid.NewGuid();
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, VendorId = 0x054c, ProdId = 0x0ce6, IsOnline = true,
                DevicePath = "persona-release-test://" + id,
            });
            var row = new UserSetting { InstanceGuid = id, MapTo = slot };
            row.SetPadSetting(ps);
            SettingsManager.UserSettings.Items.Add(row);
            return id;
        }

        private static PadSetting Gains() => new PadSetting
        {
            ForceOverall = "50", LeftMotorStrength = "100", RightMotorStrength = "80", ForceSwapMotor = "1",
        };

        [Fact]
        public void TheLevelAlone_ThroughTheRowsGainStrengthsAndSwap()
        {
            var ps = Gains();
            var id = AddDualSense(0, ps);
            PersonaHapticRumbleTests.InstallFeed(_im, 0, LfeOutputState.Pack(40000, 20000, 0, 0));
            // The game's own rumble on the slot is another source.
            _im.VibrationStates[0] = new Vibration(65535, 65535);

            var bytes = PersonaHapticRumble.ForDevice(_im, 0, id, Environment.TickCount64, new PersonaHapticRumble.DeviceScratch());

            _im.ScaleRumbleForDevice(40000, 20000, ps, out ushort l, out ushort r);
            Assert.Equal(((byte)(r >> 8), (byte)(l >> 8)), bytes);
            Assert.NotEqual(((byte)0, (byte)0), bytes);
        }

        [Fact]
        public void NoneForAPadPlayingTheTrack_OrAStaleLevel()
        {
            var id = AddDualSense(0, Gains());
            var feed = PersonaHapticRumbleTests.InstallFeed(_im, 0, LfeOutputState.Pack(40000, 20000, 0, 0));
            var scratch = new PersonaHapticRumble.DeviceScratch();
            Assert.NotEqual(((byte)0, (byte)0), PersonaHapticRumble.ForDevice(_im, 0, id, Environment.TickCount64, scratch));

            // A level older than StaleMs.
            feed.HapticLevelMs = Environment.TickCount64 - PersonaHapticRumble.StaleMs - 1000;
            Assert.Equal(((byte)0, (byte)0), PersonaHapticRumble.ForDevice(_im, 0, id, Environment.TickCount64, scratch));

            // A DualSense whose haptic ring was just read plays the track itself.
            feed.HapticLevelMs = long.MaxValue / 2;
            var ring = new AudioPassthroughService.PersonaHapticRing();
            ((System.Collections.Concurrent.ConcurrentDictionary<Guid, AudioPassthroughService.PersonaHapticRing>)
                typeof(AudioPassthroughService).GetField("_personaHapticRings", StaticPrivate)!.GetValue(null)!)[id] = ring;
            _rings.Add(id);
            ring.ReadFrames(new short[2], 1);
            Assert.Equal(((byte)0, (byte)0), PersonaHapticRumble.ForDevice(_im, 0, id, Environment.TickCount64, scratch));
        }
    }

    /// <summary>The dispatcher's pass publishes what its own frame asserts
    /// for rumble on a pass-through target, and nothing while the mirror
    /// holds a game's rumble. Drives a real pass through a temp file standing
    /// in for the pad, the EffectDeliveryTests shape.</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class PassthroughRumbleClaimDispatchTests
    {
        private const int Slot = 12;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

        private static void StopAudioWorker()
        {
            AudioPassthroughService.Shutdown();
            foreach (string name in new[] { "_workerThread", "_btThread" })
                if (typeof(AudioPassthroughService).GetField(name, Static)!.GetValue(null) is System.Threading.Thread t && t.IsAlive)
                    Assert.True(t.Join(5000));
        }

        private static void ForgetMirror(int slot)
        {
            var gate = typeof(UserEffectsDispatcher).GetField("s_externalStateLock", Static)!.GetValue(null);
            var state = (System.Collections.IDictionary)typeof(UserEffectsDispatcher).GetField("s_externalState", Static)!.GetValue(null);
            lock (gate) state.Remove(slot);
        }

        /// <summary>One pass on a DualSense pass-through target that the
        /// slot's other sources rumble at 200/100 and its persona haptics at
        /// <paramref name="persona"/>. <paramref name="body"/> gets the
        /// dispatcher, the device and a reader of the frame its next pass
        /// writes.</summary>
        private static void WithPass((byte right, byte left) persona,
            Action<UserEffectsDispatcher, Guid, Func<byte[]>> body)
        {
            StopAudioWorker();
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge-RumbleClaim-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "usb-report.bin");
            var oldDevices = SettingsManager.UserDevices;
            var oldSettings = SettingsManager.UserSettings;
            var cfgs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            var rumble = UserEffectsDispatcher.SlotRumbleForDeviceProvider;
            var personaProvider = UserEffectsDispatcher.SlotPersonaRumbleForDeviceProvider;
            var id = Guid.NewGuid();
            UserEffectsDispatcher dispatcher = null;
            try
            {
                SettingsManager.UserDevices = new();
                SettingsManager.UserSettings = new();
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
                UserEffectsDispatcher.SlotRumbleForDeviceProvider = (_, _) => ((byte)200, (byte)100);
                UserEffectsDispatcher.SlotPersonaRumbleForDeviceProvider = (_, _) => persona;
                ForgetMirror(Slot);
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = id, VendorId = 0x054c, ProdId = 0x0ce6, IsOnline = true, IsEnabled = true, DevicePath = file,
                });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = Slot });
                Assert.True(DualSensePassthroughDispatcher.IsPassthroughTarget(Slot, id));
                dispatcher = new UserEffectsDispatcher(Slot,
                    new DeviceSlotConfig { LightbarMode = LightbarMode.Off, AudioOutputPath = AudioOutputPath.Automatic },
                    startTimer: false);
                var d = dispatcher;
                body(d, id, () =>
                {
                    System.IO.File.WriteAllBytes(file, Array.Empty<byte>());
                    d.ApplyOnce();
                    return System.IO.File.ReadAllBytes(file);
                });
            }
            finally
            {
                dispatcher?.Dispose();
                ForgetMirror(Slot);
                UserEffectsDispatcher.SlotPersonaRumbleForDeviceProvider = personaProvider;
                UserEffectsDispatcher.SlotRumbleForDeviceProvider = rumble;
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = cfgs;
                SettingsManager.UserDevices = oldDevices;
                SettingsManager.UserSettings = oldSettings;
                if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                System.IO.Directory.Delete(dir);
            }
        }

        private static void AssertClaimIsTheFrame(byte[] frame, long claim)
        {
            Assert.NotEqual(0L, claim);
            Assert.Equal(frame[1] & 0x03, (int)(claim & 0xFF));
            Assert.Equal(frame[39] & 0x04, (int)((claim >> 8) & 0xFF));
            Assert.Equal(frame[3], (int)((claim >> 16) & 0xFF));
            Assert.Equal(frame[4], (int)((claim >> 24) & 0xFF));
        }

        private static byte[] GameFrame(byte vf0, byte right, byte left)
        {
            var game = new byte[47];
            game[0] = vf0;
            game[2] = right;
            game[3] = left;
            return game;
        }

        [Fact]
        public void ThePublishedClaimMatchesTheFrameAndIsEmptyWhileTheGameDrivesRumble()
        {
            WithPass((0, 0), (dispatcher, id, pass) =>
            {
                // PadForge owns the rumble: the frame asserts it, and the
                // claim carries the same bits and motors.
                byte[] own = pass();
                Assert.Equal(48, own.Length);
                Assert.Equal(0x02, own[0]);
                Assert.Equal(0x03, own[1] & 0x03);
                Assert.Equal(200, own[3]);
                Assert.Equal(100, own[4]);
                AssertClaimIsTheFrame(own, DualSensePassthroughDispatcher.CurrentRumbleClaim(id, Environment.TickCount64));

                // A game drives rumble through the pass-through: the frame
                // mirrors the game's, and nothing is claimed.
                UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x03, 90, 80));
                byte[] mirrored = pass();
                Assert.Equal(0x03, mirrored[1] & 0x03);
                Assert.Equal(90, mirrored[3]);
                Assert.Equal(80, mirrored[4]);
                Assert.Equal(0L, DualSensePassthroughDispatcher.CurrentRumbleClaim(id, Environment.TickCount64));
            });
        }

        /// <summary>G7: a game claims rumble and then releases it (a frame
        /// with every rumble bit clear, back to audio haptics), on a DualSense
        /// that takes the persona haptics as rumble. Inside the release grace
        /// the pass writes the converted haptics with PadForge's rumble bits
        /// and claims them for the pass-through lane. The slot's other sources
        /// (200/100 here) keep the grace.</summary>
        [Fact]
        public void InsideTheReleaseGrace_ADualSenseTakingTheHapticsGetsThem()
        {
            WithPass((150, 120), (dispatcher, id, pass) =>
            {
                // The first pass takes the assignment, which starts the slot
                // with no mirrored state, so the game writes after it.
                pass();
                UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x03, 90, 80));
                UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x0C, 0, 0));
                byte[] frame = pass();
                Assert.Equal(0x03, frame[1] & 0x03);
                Assert.Equal(0x04, frame[39] & 0x04);
                Assert.Equal(150, frame[3]);
                Assert.Equal(120, frame[4]);
                AssertClaimIsTheFrame(frame, DualSensePassthroughDispatcher.CurrentRumbleClaim(id, Environment.TickCount64));
            });
        }

        /// <summary>A test rumble on the slot that targets another device
        /// keeps the haptics off this one inside the grace too: the frame
        /// carries no persona rumble and nothing is claimed.</summary>
        [Fact]
        public void InsideTheReleaseGrace_AForeignTestTargetKeepsTheHapticsOff()
        {
            var testTarget = UserEffectsDispatcher.TestRumbleTargetGuidProvider;
            try
            {
                var other = Guid.NewGuid();
                UserEffectsDispatcher.TestRumbleTargetGuidProvider = slot => slot == Slot ? other : Guid.Empty;
                WithPass((150, 120), (dispatcher, id, pass) =>
                {
                    pass();
                    UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x03, 90, 80));
                    UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x0C, 0, 0));
                    byte[] frame = pass();
                    Assert.Equal(0, frame[3]);
                    Assert.Equal(0, frame[4]);
                    Assert.Equal(0L, DualSensePassthroughDispatcher.CurrentRumbleClaim(id, Environment.TickCount64));
                });
            }
            finally { UserEffectsDispatcher.TestRumbleTargetGuidProvider = testTarget; }
        }

        /// <summary>Only a release yields to the haptics. While the game
        /// drives rumble the pad gets the game's, whatever the persona
        /// level.</summary>
        [Fact]
        public void WhileTheGameDrivesRumble_TheHapticsStayOut()
        {
            WithPass((150, 120), (dispatcher, id, pass) =>
            {
                pass();
                UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x03, 90, 80));
                byte[] frame = pass();
                Assert.Equal(0x03, frame[1] & 0x03);
                Assert.Equal(90, frame[3]);
                Assert.Equal(80, frame[4]);
                Assert.Equal(0L, DualSensePassthroughDispatcher.CurrentRumbleClaim(id, Environment.TickCount64));
            });
        }

        /// <summary>Positive control for
        /// InsideTheReleaseGrace_ADualSenseTakingTheHapticsGetsThem: with no persona
        /// level the release is mirrored as it was, zero bits and zero motors,
        /// and the slot's other sources stay out for the grace.</summary>
        [Fact]
        public void InsideTheReleaseGrace_WithoutHaptics_TheReleaseIsMirrored()
        {
            WithPass((0, 0), (dispatcher, id, pass) =>
            {
                pass();
                UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x03, 90, 80));
                UserEffectsDispatcher.NotifyExternalSubsystems(Slot, GameFrame(0x0C, 0, 0));
                byte[] frame = pass();
                Assert.Equal(0, frame[1] & 0x03);
                Assert.Equal(0, frame[39] & 0x04);
                Assert.Equal(0, frame[3]);
                Assert.Equal(0, frame[4]);
                Assert.Equal(0L, DualSensePassthroughDispatcher.CurrentRumbleClaim(id, Environment.TickCount64));
            });
        }
    }
}
