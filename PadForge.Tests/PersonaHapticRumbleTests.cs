using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Haptics a game plays as audio on a DualSense persona reach every
    /// rumble device on the slot as rumble (discussion #522), through the
    /// device's Force Feedback settings, except a pad that plays the haptic
    /// track itself.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PersonaHapticRumbleTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly List<Guid> _rings = new();
        private readonly InputManager _im = new();

        public PersonaHapticRumbleTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            foreach (var g in _rings) Rings.TryRemove(g, out _);
        }

        // ── Plumbing ──

        private static ConcurrentDictionary<Guid, AudioPassthroughService.PersonaHapticRing> Rings
            => (ConcurrentDictionary<Guid, AudioPassthroughService.PersonaHapticRing>)typeof(AudioPassthroughService)
                .GetField("_personaHapticRings", StaticPrivate)!.GetValue(null)!;

        private AudioPassthroughService.PersonaFeed Feed(int slot, long level, long levelMs = long.MaxValue / 2)
            => InstallFeed(_im, slot, level, levelMs);

        /// <summary>Installs a persona feed on the slot holding
        /// <paramref name="level"/>. A time ahead of the clock keeps the level
        /// fresh however slowly the test runs.</summary>
        internal static AudioPassthroughService.PersonaFeed InstallFeed(InputManager im, int slot, long level,
            long levelMs = long.MaxValue / 2)
        {
            var feeds = (AudioPassthroughService.PersonaFeed[])typeof(InputManager)
                .GetField("_personaAudioFeeds", Private)!.GetValue(im)!;
            var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = slot };
            feed.HapticLevelMs = levelMs;
            feed.HapticLevel = level;
            feeds[slot] = feed;
            return feed;
        }

        /// <summary>A haptic ring for the device, read now when
        /// <paramref name="read"/>: the device plays the haptic track
        /// itself, the way a DualSense's USB shaper or Bluetooth tick does.</summary>
        private AudioPassthroughService.PersonaHapticRing Ring(Guid device, bool read)
        {
            var ring = new AudioPassthroughService.PersonaHapticRing();
            Rings[device] = ring;
            _rings.Add(device);
            if (read) ring.ReadFrames(new short[2], 1);
            return ring;
        }

        private UserDevice AddDevice(int slot, ushort vid = 0, ushort pid = 0, PadSetting ps = null)
        {
            var device = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), DevicePath = "persona-haptic-test://" + Guid.NewGuid(), IsOnline = true,
                CapType = InputDeviceType.Gamepad, VendorId = vid, ProdId = pid,
                Device = DispatchProxy.Create<ISdlInputDevice, MotorStub>(),
                ForceFeedbackState = new ForceFeedbackState(),
            };
            SettingsManager.UserDevices.Items.Add(device);
            var setting = new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = slot };
            setting.SetPadSetting(ps ?? new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
            return device;
        }

        private void Feedback(UserDevice device)
            => typeof(InputManager).GetMethod("ApplyForceFeedback", Private)!.Invoke(_im, new object[] { device });

        private static (ushort, ushort) Last(UserDevice device) => ((MotorStub)(object)device.Device).Sent.Last();

        public class MotorStub : DispatchProxy
        {
            public readonly List<(ushort, ushort)> Sent = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "SetRumble") { Sent.Add(((ushort)args[0], (ushort)args[1])); return true; }
                if (method.Name == "StopRumble") { Sent.Add((0, 0)); return true; }
                if (method.Name is "get_HasRumble" or "get_IsAttached") return true;
                if (method.ReturnType == typeof(void)) return null;
                return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }

        // ── The merge ──

        [Fact]
        public void TheMerge_KeepsEveryOtherField_AndNeverChangesTheIncomingObject()
        {
            var raw = new Vibration(30000, 1000)
            {
                LeftTriggerMotorSpeed = 333, RightTriggerMotorSpeed = 444,
                HasDirectionalData = true, HasConditionData = true, EffectType = 8,
                SignedMagnitude = -3000, Direction = 4000, Period = 5000, DeviceGain = 70,
                ConditionAxisCount = 1, ConditionAxes = new[] { new ConditionAxisData { Offset = 77 } },
            };
            var scratch = new Vibration();
            var result = PersonaHapticRumble.Merge(raw, LfeOutputState.Pack(20000, 25000, 0, 0), scratch);
            Assert.Same(scratch, result);
            Assert.Equal(30000, raw.LeftMotorSpeed);
            Assert.Equal(1000, raw.RightMotorSpeed);
            Assert.Equal(30000, result.LeftMotorSpeed);
            Assert.Equal(25000, result.RightMotorSpeed);
            foreach (var p in typeof(Vibration).GetProperties().Where(p => p.Name is not "LeftMotorSpeed" and not "RightMotorSpeed"))
                Assert.Equal(p.GetValue(raw), p.GetValue(result));

            Assert.Same(raw, PersonaHapticRumble.Merge(raw, 0L, scratch));
            // The trigger lanes of a level never reach the body motors.
            Assert.Same(raw, PersonaHapticRumble.Merge(raw, LfeOutputState.Pack(0, 0, 9000, 9000), scratch));
        }

        // ── A motor pad on the slot ──

        [Fact]
        public void AMotorPadOnTheSlot_RumblesWithTheHaptics_ThroughItsForceFeedbackSettings()
        {
            var pad = AddDevice(0);
            var ps = SettingsManager.UserSettings.Items[0].GetPadSetting();
            var feed = Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0));

            // The left actuator on the left motor, the right on the right.
            Feedback(pad);
            Assert.Equal(((ushort)40000, (ushort)20000), Last(pad));

            // The device's Force Feedback settings apply, and Overall Gain at 0
            // turns it off, as it does for game rumble.
            ps.ForceOverall = "50";
            Feedback(pad);
            Assert.Equal(((ushort)20000, (ushort)10000), Last(pad));
            ps.ForceSwapMotor = "1";
            Feedback(pad);
            Assert.Equal(((ushort)10000, (ushort)20000), Last(pad));
            ps.ForceOverall = "0";
            Feedback(pad);
            Assert.Equal(((ushort)0, (ushort)0), Last(pad));
            ps.ForceOverall = "100";
            ps.ForceSwapMotor = "0";

            // Game rumble and the haptics: each motor plays the louder.
            _im.VibrationStates[0].RightMotorSpeed = 30000;
            Feedback(pad);
            Assert.Equal(((ushort)40000, (ushort)30000), Last(pad));

            // A level the stream stopped refreshing is dropped.
            feed.HapticLevelMs = Environment.TickCount64 - 10 * PersonaHapticRumble.StaleMs;
            Feedback(pad);
            Assert.Equal(((ushort)0, (ushort)30000), Last(pad));
            _im.VibrationStates[0].RightMotorSpeed = 0;
            Feedback(pad);
            Assert.Equal(((ushort)0, (ushort)0), Last(pad));
        }

        [Fact]
        public void TheHaptics_CountAsTheGamesForce_SoConstantForceRests()
        {
            var ps = new PadSetting { ConstantForceEnabled = "1", ConstantForceX = "0.5", ConstantForceY = "0" };
            var pad = AddDevice(0, ps: ps);
            Feedback(pad);
            var resting = Last(pad);
            Assert.NotEqual(((ushort)0, (ushort)0), resting);

            Feed(0, LfeOutputState.Pack(1000, 1000, 0, 0));
            Feedback(pad);
            Assert.Equal(((ushort)1000, (ushort)1000), Last(pad));
        }

        [Fact]
        public void APadOnAnotherSlot_TakesNothingFromThisSlotsHaptics()
        {
            SettingsManager.SlotCreated[1] = SettingsManager.SlotEnabled[1] = true;
            var pad = AddDevice(1);
            Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0));
            Feedback(pad);
            Assert.True(((MotorStub)(object)pad.Device).Sent.All(s => s == (0, 0)));
        }

        // ── A pad that plays the haptic track itself ──

        [Fact]
        public void ADualSensePlayingTheHapticChannels_TakesNoRumble_AndOneWhoseRouteIsDownDoes()
        {
            var dualSense = Guid.NewGuid();
            var ring = Ring(dualSense, read: true);
            long t = ring.LastPolledMs + AudioPassthroughService.PersonaHapticReaderLiveMs;
            Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0), levelMs: t);
            var raw = new Vibration();
            var scratch = new Vibration();

            Assert.True(AudioPassthroughService.RendersPersonaHaptics(dualSense, t));
            Assert.Same(raw, _im.WithPersonaHaptics(0, dualSense, raw, scratch, t));

            // Nothing has read its ring for longer than that: the route is
            // down, so the pad takes the haptics as rumble, as chiaki-ng does
            // when no DualSense plays them.
            Assert.False(AudioPassthroughService.RendersPersonaHaptics(dualSense, t + 1));
            var merged = _im.WithPersonaHaptics(0, dualSense, raw, scratch, t + 1);
            Assert.Same(scratch, merged);
            Assert.Equal(40000, merged.LeftMotorSpeed);
            Assert.Equal(20000, merged.RightMotorSpeed);

            // A DualShock 4 on the slot gets a ring that nothing reads.
            var ds4 = Guid.NewGuid();
            var ds4Ring = Ring(ds4, read: false);
            Assert.False(AudioPassthroughService.RendersPersonaHaptics(ds4, t));
            Assert.Same(scratch, _im.WithPersonaHaptics(0, ds4, raw, scratch, t));
            Assert.False(AudioPassthroughService.RendersPersonaHaptics(Guid.Empty, t));

            // The Bluetooth tick polls before a whole tick is waiting, and a
            // poll alone marks the pad as playing the haptics.
            ds4Ring.NotePolled();
            Assert.True(AudioPassthroughService.RendersPersonaHaptics(ds4, ds4Ring.LastPolledMs));
        }

        [Fact]
        public void TheBluetoothTick_MarksTheRingPolled_BeforeItWaitsForAWholeTick()
        {
            string aps = Source("Common", "Input", "AudioPassthroughService.cs");
            int fn = aps.IndexOf("private static bool TryTakeDs5HapticTick(", StringComparison.Ordinal);
            int lookup = aps.IndexOf("if (!_personaHapticRings.TryGetValue(s.DeviceGuid, out var ring)) return false;", fn, StringComparison.Ordinal);
            int poll = aps.IndexOf("ring.NotePolled();", fn, StringComparison.Ordinal);
            int whole = aps.IndexOf("if (ring.FramesAvailable < Ds5HapticFramesPerTick) return false;", fn, StringComparison.Ordinal);
            Assert.True(fn > 0 && lookup > fn && poll > lookup && whole > poll,
                "the tick marks the ring polled before the whole-tick check, so a starting stream counts");
        }

        [Fact]
        public void ADualShock4_PlaysPadForgesBytes_WhileAGameReleasesRumbleForHaptics()
        {
            // A claim mirrors the game's bytes, as before.
            var claim = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                RumbleRight = 50, RumbleLeft = 60, RumbleFlag0Bits = 0x03,
            };
            var fields = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f,
                rumbleRight: 100, rumbleLeft: 200, assertRumbleEnable: false, overrides: claim);
            Assert.Equal((byte)50, fields["rightMotor"]);
            Assert.Equal((byte)60, fields["leftMotor"]);

            // A release, every rumble bit clear, hands the motors to
            // PadForge's own bytes, the haptics as rumble among them, under
            // the release's enable bit, so a zero still stops the pad.
            var release = new UserEffectsDispatcher.ExternalSubsystemOverrides { RumbleRight = 0, RumbleLeft = 0 };
            fields = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f,
                rumbleRight: 100, rumbleLeft: 200, assertRumbleEnable: false, overrides: release);
            Assert.Equal((byte)100, fields["rightMotor"]);
            Assert.Equal((byte)200, fields["leftMotor"]);
            Assert.Equal(0x01, (byte)fields["validFlag0"] & 0x01);
            fields = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f,
                rumbleRight: 0, rumbleLeft: 0, assertRumbleEnable: false, overrides: release);
            Assert.Equal((byte)0, fields["rightMotor"]);
            Assert.Equal((byte)0, fields["leftMotor"]);
            Assert.Equal(0x01, (byte)fields["validFlag0"] & 0x01);

            // An improved-emulation claim selects rumble through valid_flag2
            // alone, and stays a claim.
            var improved = new UserEffectsDispatcher.ExternalSubsystemOverrides
            {
                RumbleRight = 70, RumbleLeft = 80, RumbleFlag2Bits = 0x04,
            };
            fields = Ds4EffectSynthesizer.BuildFields(new DeviceSlotConfig(), 0f, 0, 0, 0, 0f,
                rumbleRight: 100, rumbleLeft: 200, assertRumbleEnable: false, overrides: improved);
            Assert.Equal((byte)70, fields["rightMotor"]);
            Assert.Equal((byte)80, fields["leftMotor"]);
        }

        [Fact]
        public void TheSteamAndSwitchSnapshot_FollowsThePersonaSinks()
        {
            string hts = File.ReadAllText(Path.Combine(BassShakerPersonaHapticsTests.RepoRoot(),
                "PadForge.App", "Common", "Input", "HapticToneService.cs"));
            int recompute = hts.IndexOf("private static void RecomputePersonaMask()", StringComparison.Ordinal);
            int add = hts.IndexOf("pairs.Add((s.Slot, s.DeviceGuid));", recompute, StringComparison.Ordinal);
            int publish = hts.IndexOf("Volatile.Write(ref _personaSinks, pairs.ToArray());", recompute, StringComparison.Ordinal);
            int end = hts.IndexOf("internal static bool RendersPersonaHaptics(int slot, Guid device)", recompute, StringComparison.Ordinal);
            Assert.True(recompute > 0 && add > recompute && publish > add && end > publish,
                "the snapshot is rebuilt from the PersonaOn sinks wherever the mask is");
            Assert.Contains("if (s.PersonaOn && s.Slot >= 0 && s.Slot < 32)", hts.Substring(recompute, add - recompute));
        }

        // ── The meters ──

        [Fact]
        public void TheMeters_ShowWhatEachDeviceIsSent()
        {
            var pad = AddDevice(0);
            var dualSense = AddDevice(0);
            Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0));
            Ring(dualSense.InstanceGuid, read: true);

            _im.SelectedDeviceGuids[0] = dualSense.InstanceGuid;
            _im.ComputeFinalVibrationStates();
            Assert.Equal(40000, _im.FinalVibrationStates[0].LeftMotorSpeed);
            Assert.Equal(20000, _im.FinalVibrationStates[0].RightMotorSpeed);
            Assert.Equal(0, _im.SelectedDeviceVibrationStates[0].LeftMotorSpeed);
            Assert.Equal(0, _im.SelectedDeviceVibrationStates[0].RightMotorSpeed);

            _im.SelectedDeviceGuids[0] = pad.InstanceGuid;
            _im.ComputeFinalVibrationStates();
            Assert.Equal(40000, _im.SelectedDeviceVibrationStates[0].LeftMotorSpeed);
            Assert.Equal(20000, _im.SelectedDeviceVibrationStates[0].RightMotorSpeed);

            // A slot with no device shows the haptics as it shows game rumble.
            SettingsManager.UserSettings.Items.Clear();
            _im.ComputeFinalVibrationStates();
            Assert.Equal(40000, _im.FinalVibrationStates[0].LeftMotorSpeed);
            Assert.Equal(20000, _im.FinalVibrationStates[0].RightMotorSpeed);
        }

        // ── The Sony dispatcher ──

        [Fact]
        public void TheSonyDispatcher_WakesForADualShock4_AndNotForADualSensePlayingTheHaptics()
        {
            var refresh = typeof(InputManager).GetMethod("RefreshSonyPokeCfg", Private)!;
            var takes = typeof(InputManager).GetMethod("SonyPadTakesPersonaHaptics", Private)!;
            bool Takes() => (bool)takes.Invoke(_im, new object[] { 0, Environment.TickCount64 })!;
            void Refresh() => refresh.Invoke(_im, new object[] { SettingsManager.UserSettings });

            Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0));
            var dualSense = AddDevice(0, 0x054C, 0x0CE6);
            Ring(dualSense.InstanceGuid, read: true);
            Refresh();
            Assert.False(Takes());

            // A DualShock 3 is a Sony pad whose rumble goes through SDL.
            AddDevice(0, 0x054C, 0x0268);
            Refresh();
            Assert.False(Takes());

            var ds4 = AddDevice(0, 0x054C, 0x05C4);
            Refresh();
            Assert.True(Takes());

            // No level, no wake.
            Feed(0, 0L);
            Assert.False(Takes());

            // The dispatcher's change detection reads the level only for a
            // slot the poke pass marked.
            Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0));
            var bits = typeof(InputManager).GetField("_sonyPersonaHapticSlots", Private)!;
            bits.SetValue(_im, 1);
            Assert.Equal(LfeOutputState.Pack(40000, 20000, 0, 0), _im.SonyPersonaHapticLevel(0, Environment.TickCount64));
            bits.SetValue(_im, 0);
            Assert.Equal(0L, _im.SonyPersonaHapticLevel(0, Environment.TickCount64));
            Assert.True(TouchpadPulseService.IsSonyRumblePad(ds4));
        }

        [Fact]
        public void ThePokePass_MarksAndWakes_ForEverySlotADispatcherPadTakesTheHapticsOn()
        {
            string step2 = Source("Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int pass1 = step2.IndexOf("int needGameMask = 0, needAudioMask = 0, sonyHapticMask = 0;", StringComparison.Ordinal);
            int check = step2.IndexOf("if (SonyPadTakesPersonaHaptics(padIndex, pokeNow))", pass1, StringComparison.Ordinal);
            int mark = step2.IndexOf("sonyHapticMask |= 1 << padIndex;", check, StringComparison.Ordinal);
            int wake = step2.IndexOf("hasGameRumble = true;", mark, StringComparison.Ordinal);
            int close = step2.IndexOf("}", mark, StringComparison.Ordinal);
            int need = step2.IndexOf("if (hasGameRumble) needGameMask |= 1 << padIndex;", check, StringComparison.Ordinal);
            int write = step2.IndexOf("Volatile.Write(ref _sonyPersonaHapticSlots, sonyHapticMask);", need, StringComparison.Ordinal);
            int pass2 = step2.IndexOf("// Pass 2: group-OR poke.", pass1, StringComparison.Ordinal);
            Assert.True(pass1 > 0 && check > pass1 && mark > check && wake > mark && close > wake
                && need > close && write > need && pass2 > write,
                "the poke marks the slot and counts the haptics as game rumble before the group OR");
        }

        // ── The haptic mirror's Rumble engage mode ──

        [Fact]
        public void TheMirrorsRumbleMode_Engages_WhileThePersonaHapticsPlay()
        {
            var device = Guid.NewGuid();
            _im.HapticMirrorEngageConfigProvider = slot => slot == 0
                ? new List<(Guid Device, int Mode, string EngageDeviceGuid, string Button, int ReleaseMs)> { (device, 2, "", "", 0) }
                : null;
            var update = typeof(InputManager).GetMethod("UpdateHapticMirrorEngageStates", Private)!;
            var cell = HapticToneService.GetOrCreateEngageCell(0, device);

            update.Invoke(_im, null);
            Assert.False(cell.Engaged);

            Feed(0, LfeOutputState.Pack(40000, 20000, 0, 0));
            update.Invoke(_im, null);
            Assert.True(cell.Engaged);
        }

        // ── The Rumble source family ──

        /// <summary>The slot's inbound pack, which the Rumble source family
        /// and the shaker lane read, takes the haptics on its two body voices
        /// as a max, and leaves the game's trigger voices alone.</summary>
        [Fact]
        public void TheInboundPack_TakesTheHapticsOnTheBodyVoices()
        {
            long now = Environment.TickCount64;
            long game = LfeOutputState.Pack(50000, 10000, 7, 9);
            Assert.Equal(0L, _im.WithPersonaHaptics(0, 0L, now));
            Assert.Equal(game, _im.WithPersonaHaptics(0, game, now));

            Feed(0, LfeOutputState.Pack(40000, 20000, 1234, 4321));
            Assert.Equal(LfeOutputState.Pack(40000, 20000, 0, 0), _im.WithPersonaHaptics(0, 0L, now));
            Assert.Equal(LfeOutputState.Pack(50000, 20000, 7, 9), _im.WithPersonaHaptics(0, game, now));
            // Another slot's feed reaches nothing here.
            Assert.Equal(0L, _im.WithPersonaHaptics(1, 0L, now));
        }

        // ── Every reader of a slot's rumble takes the persona layer ──

        /// <summary>The sibling set (discussion #522): a game's haptics are
        /// its rumble on a DualSense persona slot, so every read of a slot's
        /// vibration state, and every read of its inbound pack, is followed by
        /// the persona layer. A new reader that skips it fails here: route it
        /// through <c>InputManager.WithPersonaHaptics</c> (per device, or per
        /// slot on a pack) or <c>PersonaHapticLevel</c> (per slot), or list it
        /// below with the reason it does not read rumble.</summary>
        [Fact]
        public void EveryReaderOfASlotsRumble_TakesThePersonaHaptics()
        {
            var exempt = new Dictionary<string, string>
            {
                // The VC destroy path zeroes the slot's state.
                ["Common/Input/InputManager.Step5.VirtualDevices.cs|var vib = VibrationStates[padIndex];"] = "writer",
                // Test rumble and the steering pulse write into the state.
                ["Services/InputService.cs|var vib = _inputManager.VibrationStates[padIndex];"] = "writer",
            };
            var persona = new Regex(@"WithPersonaHaptics\(|PersonaHapticLevel\(|SonyPadTakesPersonaHaptics\(");
            var read = new Regex(@"=\s*(_inputManager\.)?VibrationStates\[[^\]]+\];");
            // The inbound pack's readers, its definition aside.
            var packRead = new Regex(@"(?<!public long )GetInboundRumblePack\(");
            string app = Path.Combine(BassShakerPersonaHapticsTests.RepoRoot(), "PadForge.App");
            var missing = new List<string>();
            int readers = 0, packReaders = 0;
            foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                string rel = Path.GetRelativePath(app, file).Replace('\\', '/');
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    Regex kind;
                    if (read.IsMatch(lines[i]))
                    {
                        if (exempt.ContainsKey(rel + "|" + lines[i].Trim())) continue;
                        kind = read;
                        readers++;
                    }
                    else if (packRead.IsMatch(lines[i]))
                    {
                        kind = packRead;
                        packReaders++;
                    }
                    else continue;
                    // The layer follows within 40 lines and before the next
                    // read of the same kind, so one reader's call cannot stand
                    // in for another's.
                    bool found = false;
                    for (int j = i; j < Math.Min(lines.Length, i + 40) && !found; j++)
                    {
                        if (j > i && kind.IsMatch(lines[j])) break;
                        found = persona.IsMatch(lines[j]);
                    }
                    if (!found) missing.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                }
            }
            Assert.True(missing.Count == 0, "readers without the persona layer:\n" + string.Join("\n", missing));
            // ApplyForceFeedback, the meters, the poke, the shaker lane, the
            // mirror gate, the UI raw bar and the dispatcher's three providers
            // (two of them with a row-less fallback).
            Assert.Equal(11, readers);
            // The shaker lane and the Rumble source family's provider.
            Assert.Equal(2, packReaders);
        }

        [Fact]
        public void TheLayer_RunsAheadOfMacroRumble_AtEveryPerDeviceReader()
        {
            string step2 = Source("Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int aff = step2.IndexOf("private void ApplyForceFeedback(UserDevice ud)", StringComparison.Ordinal);
            int sonySkip = step2.IndexOf("|| ud.ProdId == 0x0BA0)) // DS4 wireless adapter", aff, StringComparison.Ordinal);
            int layer = step2.IndexOf("raw = WithPersonaHaptics(padIndex, ud.InstanceGuid, raw, _personaHapticScratch, hapticNow);", aff, StringComparison.Ordinal);
            int macro = step2.IndexOf("var withMacro = ResolveUserRumble(padIndex, devicePs, raw, _macroRumbleScratch,", aff, StringComparison.Ordinal);
            Assert.True(aff > 0 && sonySkip > aff && layer > sonySkip && macro > layer,
                "ApplyForceFeedback layers the haptics on before the macro merge");

            int meter = step2.IndexOf("public void ComputeFinalVibrationStates()", StringComparison.Ordinal);
            int rowLayer = step2.IndexOf("var rowRaw = WithPersonaHaptics(padIndex, us.InstanceGuid, raw, _personaHapticMeterScratch, meterNow);", meter, StringComparison.Ordinal);
            int rowMacro = step2.IndexOf("withMacro = ResolveUserRumble(padIndex, devicePs, rowRaw, _macroRumbleScratch,", meter, StringComparison.Ordinal);
            Assert.True(meter > 0 && rowLayer > meter && rowMacro > rowLayer, "the meter rows take the same layer first");

            string svc = Source("Services", "InputService.cs");
            int main = svc.IndexOf("UserEffectsDispatcher.SlotRumbleForDeviceProvider = (padIndex, deviceGuid) =>", StringComparison.Ordinal);
            int mainRow = svc.IndexOf("slotRaw = _inputManager.WithPersonaHaptics(slot, deviceGuid,", main, StringComparison.Ordinal);
            int mainMacro = svc.IndexOf("var withMacro = _inputManager.ResolveUserRumble(slot, rowPs,", main, StringComparison.Ordinal);
            int mainFallback = svc.IndexOf("raw = _inputManager.WithPersonaHaptics(padIndex, deviceGuid,", main, StringComparison.Ordinal);
            int fallbackMacro = svc.IndexOf("var withMacro = MacroRumbleOverride.Merge(raw,", main, StringComparison.Ordinal);
            Assert.True(main > 0 && mainRow > main && mainMacro > mainRow && mainFallback > mainMacro && fallbackMacro > mainFallback,
                "the Sony main-motor provider layers the haptics on in its rows and its fallback");

            int trig = svc.IndexOf("UserEffectsDispatcher.SlotImpulseTriggerForDeviceProvider = (padIndex, deviceGuid) =>", StringComparison.Ordinal);
            int trigRow = svc.IndexOf("slotRaw = _inputManager.WithPersonaHaptics(slot, deviceGuid,", trig, StringComparison.Ordinal);
            int trigResolve = svc.IndexOf("slotRaw, rowPs, _constantTriggerForceScratchSony);", trig, StringComparison.Ordinal);
            int trigFallback = svc.IndexOf("raw = _inputManager.WithPersonaHaptics(padIndex, deviceGuid,", trig, StringComparison.Ordinal);
            int trigFallbackResolve = svc.IndexOf("raw, null, _constantTriggerForceScratchSony);", trig, StringComparison.Ordinal);
            Assert.True(trig > 0 && trigRow > trig && trigResolve > trigRow && trigFallback > trigResolve && trigFallbackResolve > trigFallback,
                "the trigger provider's main-motor route reads the same rumble");
        }

        private static string Source(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { BassShakerPersonaHapticsTests.RepoRoot(), "PadForge.App" }.Concat(parts).ToArray()));
    }

    /// <summary>The Steam and Switch case reads HapticToneService's sink
    /// snapshot, which a sink teardown in the lifecycle tests rebuilds, so it
    /// runs in their collection.</summary>
    [Collection("AudioProfileLifecycle")]
    public sealed class PersonaHapticRumbleSteamSinkTests : IDisposable
    {
        private static readonly FieldInfo PersonaSinks
            = typeof(HapticToneService).GetField("_personaSinks", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object _saved = PersonaSinks.GetValue(null);

        public void Dispose() => PersonaSinks.SetValue(null, _saved);

        [Fact]
        public void ASteamOrSwitchPadPlayingTheHaptics_TakesNoRumble_OnThatSlotOnly()
        {
            var im = new InputManager();
            var steam = Guid.NewGuid();
            PersonaSinks.SetValue(null, new (int Slot, Guid Device)[] { (0, steam) });
            PersonaHapticRumbleTests.InstallFeed(im, 0, LfeOutputState.Pack(40000, 20000, 0, 0));
            PersonaHapticRumbleTests.InstallFeed(im, 1, LfeOutputState.Pack(40000, 20000, 0, 0));
            long now = Environment.TickCount64;
            var raw = new Vibration();
            var scratch = new Vibration();

            Assert.True(HapticToneService.RendersPersonaHaptics(0, steam));
            Assert.False(PersonaHapticRumble.TakesRumble(0, steam, now));
            Assert.Same(raw, im.WithPersonaHaptics(0, steam, raw, scratch, now));

            Assert.False(HapticToneService.RendersPersonaHaptics(1, steam));
            Assert.Same(scratch, im.WithPersonaHaptics(1, steam, raw, scratch, now));
        }
    }
}
