using System;
using System.IO;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Bass Shakers on a composite DualSense persona (discussion #522). A game
    /// that plays the DualSense's haptics through the persona's audio sends no
    /// rumble, so the slot's Rumble to Audio voices follow the loudness of the
    /// two haptic channels: the left on the low voice, the right on the high.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class BassShakerPersonaHapticsTests : IDisposable
    {
        private const int Slot = 14;
        private const int Rate = 48000;
        // The DualSense persona's window: speaker left, speaker right,
        // haptic left, haptic right, s16 each (dualsense-composite.json).
        private const int Stride = 8;
        private const int HapticLeftOffset = 4;
        private const int HapticRightOffset = 6;

        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;

        public void Dispose()
        {
            SettingsManager.SlotMappingSets = _sets;
            RumbleAudioService.SilenceSlot(Slot);
        }

        /// <summary>A 4-channel window of <paramref name="frames"/> frames:
        /// each channel a sine at its own amplitude (0 to 1) and frequency.</summary>
        internal static byte[] Window(int frames, int startFrame,
            double speakerAmp, double hapticLeftAmp, double hapticRightAmp, double hz = 100)
        {
            var pcm = new byte[frames * Stride];
            for (int f = 0; f < frames; f++)
            {
                double s = Math.Sin(2 * Math.PI * hz * (startFrame + f) / Rate);
                Put(pcm, f * Stride + 0, speakerAmp * s);
                Put(pcm, f * Stride + 2, speakerAmp * s);
                Put(pcm, f * Stride + HapticLeftOffset, hapticLeftAmp * s);
                Put(pcm, f * Stride + HapticRightOffset, hapticRightAmp * s);
            }
            return pcm;
        }

        private static void Put(byte[] pcm, int at, double v)
        {
            short x = (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue);
            pcm[at] = (byte)(x & 0xFF);
            pcm[at + 1] = (byte)((x >> 8) & 0xFF);
        }

        /// <summary>Feeds <paramref name="ms"/> milliseconds in 4 ms windows
        /// and returns the last pack.</summary>
        private static long Follow(ref double l, ref double r, int ms,
            double speakerAmp, double hapticLeftAmp, double hapticRightAmp, ref int frame)
        {
            long pack = 0;
            const int windowFrames = Rate * 4 / 1000;
            for (int t = 0; t < ms; t += 4)
            {
                var w = Window(windowFrames, frame, speakerAmp, hapticLeftAmp, hapticRightAmp);
                pack = PersonaHapticRumble.Follow(ref l, ref r, w, Stride,
                    HapticLeftOffset, HapticRightOffset, Rate);
                frame += windowFrames;
            }
            return pack;
        }

        private static MappingSet[] SetsWithShaker(bool enabled)
        {
            var sets = new MappingSet[InputManager.MaxPads];
            for (int i = 0; i < sets.Length; i++) sets[i] = new MappingSet();
            sets[Slot].RumbleAudio = new RumbleAudioConfig { Enabled = enabled };
            return sets;
        }

        [Fact]
        public void TheHapticChannels_DriveTheLowAndHighVoices_AndTheSpeakersDoNot()
        {
            double l = 0, r = 0;
            int frame = 0;
            long pack = Follow(ref l, ref r, 200, speakerAmp: 1.0, hapticLeftAmp: 1.0, hapticRightAmp: 0.0, ref frame);
            Assert.InRange((int)LfeOutputState.Low(pack), 62000, 65535);
            Assert.Equal(0, LfeOutputState.High(pack));
            Assert.Equal(0, LfeOutputState.TriggerLeft(pack));
            Assert.Equal(0, LfeOutputState.TriggerRight(pack));

            // The right actuator alone, at half scale, reads about half on the
            // high voice.
            l = r = 0;
            frame = 0;
            pack = Follow(ref l, ref r, 200, speakerAmp: 1.0, hapticLeftAmp: 0.0, hapticRightAmp: 0.5, ref frame);
            Assert.Equal(0, LfeOutputState.Low(pack));
            Assert.InRange((int)LfeOutputState.High(pack), 31000, 34500);
        }

        [Fact]
        public void TheVoices_FallSilent_SoonAfterTheHapticsStop()
        {
            double l = 0, r = 0;
            int frame = 0;
            long pack = Follow(ref l, ref r, 200, 0, 1.0, 1.0, ref frame);
            Assert.NotEqual(0L, pack);
            pack = Follow(ref l, ref r, 200, 0, 0, 0, ref frame);
            Assert.Equal(0L, pack);
        }

        [Fact]
        public void QuietHaptics_UnderTheSilenceFloor_PlayNothing()
        {
            double l = 0, r = 0;
            int frame = 0;
            long pack = Follow(ref l, ref r, 200, 0, 0.01, 0.01, ref frame);
            Assert.Equal(0L, pack);
        }

        [Fact]
        public void AWindowThatDoesNotHoldItsOffsets_ReadsNothing()
        {
            double l = 0.5, r = 0.5;
            var w = Window(192, 0, 0, 1, 1);
            Assert.Equal(0L, PersonaHapticRumble.Follow(ref l, ref r, w, 6, 4, 6, Rate));
            Assert.Equal(0L, PersonaHapticRumble.Follow(ref l, ref r, w, 0, 4, 6, Rate));
            Assert.Equal(0L, PersonaHapticRumble.Follow(ref l, ref r, w, Stride, -2, 6, Rate));
            Assert.Equal(0.5, l);
        }

        [Fact]
        public void APersonaLevel_CountsOnlyWhileItIsFresh()
        {
            long level = LfeOutputState.Pack(20000, 25000, 0, 0);
            Assert.Equal(level, PersonaHapticRumble.Fresh(level, levelMs: 1000, nowMs: 1000 + PersonaHapticRumble.StaleMs));

            // Stale: the host closed the stream or the persona went away.
            Assert.Equal(0L, PersonaHapticRumble.Fresh(level, 1000, 1001 + PersonaHapticRumble.StaleMs));
            Assert.Equal(0L, PersonaHapticRumble.Fresh(0L, 1000, 1000));
        }

        [Fact]
        public void ThePersonaFollow_RunsWhateverTheShakerCard_AndPublishesEveryTenMilliseconds()
        {
            var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = Slot, HapL = 2, HapR = 3, SrcRate = Rate };
            var w = Window(192, 0, 0, 1, 1);

            // The level feeds the slot's rumble as well as its shaker, so the
            // follower runs with the card off.
            SettingsManager.SlotMappingSets = SetsWithShaker(enabled: false);
            const long T = 1_000_000;
            for (int i = 0; i < 50; i++)
                AudioPassthroughService.FollowPersonaHaptics(feed, w, Stride, T + 10 * i);
            Assert.NotEqual(0, LfeOutputState.Low(feed.HapticLevel));
            Assert.NotEqual(0, LfeOutputState.High(feed.HapticLevel));
            Assert.Equal(T + 490, feed.HapticLevelMs);

            // Windows inside the interval keep following and publish nothing,
            // so motors are written no faster than a game's own rumble.
            long published = feed.HapticLevel;
            double meanSquare = feed.HapticMeanSquareL;
            var silence = Window(192, 0, 0, 0, 0);
            AudioPassthroughService.FollowPersonaHaptics(feed, silence, Stride, T + 499);
            Assert.Equal(published, feed.HapticLevel);
            Assert.Equal(T + 490, feed.HapticLevelMs);
            Assert.True(feed.HapticMeanSquareL < meanSquare, "the follower kept following inside the interval");
            AudioPassthroughService.FollowPersonaHaptics(feed, silence, Stride, T + 500);
            Assert.Equal(T + 500, feed.HapticLevelMs);
            Assert.True(LfeOutputState.Low(feed.HapticLevel) < LfeOutputState.Low(published));
        }

        [Fact]
        public void TheFeedbackLane_PlaysTheSlotsPersonaHaptics_OnItsShaker()
        {
            var im = new InputManager();
            var feeds = (AudioPassthroughService.PersonaFeed[])typeof(InputManager)
                .GetField("_personaAudioFeeds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(im)!;
            var lane = typeof(InputManager).GetMethod("UpdateRumbleAudioLane", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = Slot };
            feeds[Slot] = feed;
            SettingsManager.SlotMappingSets = SetsWithShaker(enabled: true);

            feed.HapticLevelMs = Environment.TickCount64;
            feed.HapticLevel = LfeOutputState.Pack(40000, 20000, 0, 0);
            lane.Invoke(im, null);
            Assert.Equal(LfeOutputState.Pack(40000, 20000, 0, 0), RumbleAudioService.ReadPack(Slot));

            // A turned-off voice stays silent, as it does for rumble.
            SettingsManager.SlotMappingSets[Slot].RumbleAudio.Voices.Add(new RumbleAudioVoice
            {
                Source = RumbleAudioConfig.SourceOrder[0],
                Enabled = false,
            });
            lane.Invoke(im, null);
            Assert.Equal(LfeOutputState.Pack(0, 20000, 0, 0), RumbleAudioService.ReadPack(Slot));

            // A level the stream stopped refreshing is dropped.
            feed.HapticLevelMs = Environment.TickCount64 - 10 * PersonaHapticRumble.StaleMs;
            lane.Invoke(im, null);
            Assert.Equal(0L, RumbleAudioService.ReadPack(Slot));

            // A slot whose card is off publishes nothing.
            feed.HapticLevelMs = Environment.TickCount64;
            SettingsManager.SlotMappingSets = SetsWithShaker(enabled: false);
            lane.Invoke(im, null);
            Assert.Equal(0L, RumbleAudioService.ReadPack(Slot));
        }

        [Fact]
        public void TheFollow_RunsBeforeTheSonyTargetGate_AndTheMergeBeforeTheVoiceMasks()
        {
            string aps = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "AudioPassthroughService.cs"));
            int fn = aps.IndexOf("private static void RoutePersonaFrames", StringComparison.Ordinal);
            int follow = aps.IndexOf("FollowPersonaHaptics(feed, span, stride, now);", fn, StringComparison.Ordinal);
            int gate = aps.IndexOf("if (targets.Length == 0) return;", fn, StringComparison.Ordinal);
            Assert.True(fn > 0 && follow > fn && gate > follow,
                "the follow must run before the Sony-target gate, so a slot with no Sony pad still follows");

            string vd = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));
            int laneAt = vd.IndexOf("private void UpdateRumbleAudioLane()", StringComparison.Ordinal);
            int merge = vd.IndexOf("pack = WithPersonaHaptics(slot, pack, now);", laneAt, StringComparison.Ordinal);
            int masks = vd.IndexOf("pack &= ~(0xFFFFL << (idx * 16));", laneAt, StringComparison.Ordinal);
            int publish = vd.IndexOf("RumbleAudioService.PublishIfCurrent(slot, gen, pack);", laneAt, StringComparison.Ordinal);
            Assert.True(laneAt > 0 && merge > laneAt && masks > merge && publish > masks,
                "the persona merge must come before the voice masks and the publish");
        }

        internal static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "PadForge.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("PadForge.sln not found above " + AppContext.BaseDirectory);
        }
    }
}
