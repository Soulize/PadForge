using System;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    [Collection("FlydigiSwitchStatics")]
    public class FlydigiVader5ProFourMotorTests
    {
        private static FlydigiVader5ProHidWriter.Motors Motors(
            byte gl, byte gr, byte tl, byte tr) => new(gl, gr, tl, tr);

        [Fact]
        public void Identity_IsStrictlyVader5Pro()
        {
            Assert.True(FlydigiVader5ProHidWriter.IsDevice(0x37D7, 0x2401));
            Assert.False(FlydigiVader5ProHidWriter.IsDevice(0x37D7, 0x2402));
            Assert.False(FlydigiVader5ProHidWriter.IsDevice(0x045E, 0x2401));
            Assert.False(FlydigiVader5ProHidWriter.IsDevice(0x37D7, 0));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(128, 0)]
        [InlineData(257, 1)]
        [InlineData(32768, 128)]
        [InlineData(65535, 255)]
        public void Conversion_PreservesEightBitDynamicRange(int input, int expected)
        {
            Assert.Equal(expected, FlydigiVader5ProHidWriter.ToByte((ushort)input));
        }

        [Fact]
        public void Report_HasWindowsReportIdV2HeaderLengthAndChecksum()
        {
            var report = FlydigiVader5ProHidWriter.BuildReport(Motors(12, 34, 56, 78));
            Assert.Equal(33, report.Length);
            Assert.Equal(new byte[] { 0x06, 0x5A, 0xA5, 0x12, 0x06, 12, 34, 56, 78 },
                report.Take(9).ToArray());
            Assert.Equal(unchecked((byte)(0x12 + 6 + 12 + 34 + 56 + 78)), report[9]);
            Assert.All(report.Skip(10), value => Assert.Equal((byte)0, value));
        }

        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(255, 255, 255, 255)]
        [InlineData(0, 0, 255, 0)]
        [InlineData(0, 0, 0, 255)]
        [InlineData(255, 0, 0, 0)]
        [InlineData(0, 255, 0, 0)]
        public void Report_WritesAllFourIndependentMotorFields(
            byte leftGrip, byte rightGrip, byte leftTrigger, byte rightTrigger)
        {
            var r = FlydigiVader5ProHidWriter.BuildReport(
                Motors(leftGrip, rightGrip, leftTrigger, rightTrigger));
            Assert.Equal(leftGrip, r[5]);
            Assert.Equal(rightGrip, r[6]);
            Assert.Equal(leftTrigger, r[7]);
            Assert.Equal(rightTrigger, r[8]);
            int checksum = 0;
            for (int i = 3; i <= 8; i++) checksum += r[i];
            Assert.Equal((byte)checksum, r[9]);
        }

        private static VendorHidCollection Control(string path, ushort vid = 0x37D7,
            ushort pid = 0x2401, ushort page = 0xFFA0, ushort usage = 1)
            => new() { Path = path, VendorId = vid, ProductId = pid,
                UsagePage = page, Usage = usage, InputReportLength = 32 };

        [Fact]
        public void Discovery_RejectsUnrelatedHidCollections()
        {
            Assert.Null(FlydigiVader5ProHidWriter.SelectUniqueControlPath(null));
            Assert.Null(FlydigiVader5ProHidWriter.SelectUniqueControlPath(new[]
            {
                Control("wrong-pid", pid: 0x2402),
                Control("wrong-usage-page", page: 0xFFEF),
                Control("wrong-usage", usage: 2),
                Control("wrong-vid", vid: 0x045E),
            }));
        }

        [Fact]
        public void Discovery_AcceptsOnlyOnePhysicalInterface()
        {
            Assert.Equal(@"\\?\hid#vid_37d7&pid_2401#sample",
                FlydigiVader5ProHidWriter.SelectUniqueControlPath(new[]
                {
                    Control("other", pid: 0x2402),
                    Control(@"\\?\hid#vid_37d7&pid_2401#sample"),
                }));
        }

        [Fact]
        public void Discovery_RejectsMultipleControllersRatherThanCrossWiring()
        {
            Assert.Null(FlydigiVader5ProHidWriter.SelectUniqueControlPath(new[]
            {
                Control("physical-pad-1"), Control("physical-pad-2"),
            }));
            Assert.Equal("same-pad", FlydigiVader5ProHidWriter.SelectUniqueControlPath(
                new[] { Control("same-pad"), Control("SAME-PAD") }));
        }

        [Fact]
        public void Discovery_RejectsMissingDevicePath()
        {
            Assert.Null(FlydigiVader5ProHidWriter.SelectUniqueControlPath(new[]
                { Control(""), Control(null) }));
        }

        [Fact]
        public void Safety_InputBecomesStaleAfter100Ms()
        {
            Assert.Equal(100, FlydigiVader5ProHidWriter.StaleInputMs);
            Assert.False(FlydigiVader5ProHidWriter.IsFresh(0, 0));
            Assert.False(FlydigiVader5ProHidWriter.IsFresh(100, 0));
            Assert.True(FlydigiVader5ProHidWriter.IsFresh(101, 1));
            Assert.False(FlydigiVader5ProHidWriter.IsFresh(102, 1));
            Assert.False(FlydigiVader5ProHidWriter.IsFresh(1, 200));
        }

        [Fact]
        public void Safety_PacesWritesToNoMoreThan33Point4PerSecond()
        {
            Assert.True(FlydigiVader5ProHidWriter.MaySend(0, 0, false));
            Assert.False(FlydigiVader5ProHidWriter.MaySend(1029, 1000, true));
            Assert.True(FlydigiVader5ProHidWriter.MaySend(1030, 1000, true));
            Assert.True(FlydigiVader5ProHidWriter.MaySend(999, 1000, true));
            Assert.Equal(30, FlydigiVader5ProHidWriter.MinimumWriteIntervalMs);
            Assert.True(FlydigiVader5ProHidWriter.StaleInputMs + FlydigiVader5ProHidWriter.MinimumWriteIntervalMs <= 300);
        }

        [Theory]
        [InlineData(10, 9, false)]
        [InlineData(10, 10, true)]
        [InlineData(20, 19, false)]
        [InlineData(20, 20, true)]
        [InlineData(60, 59, false)]
        [InlineData(60, 60, true)]
        public void Timing_CustomIntervalIsApplied(int interval, long elapsed, bool expected)
        {
            Assert.Equal(expected,
                FlydigiVader5ProHidWriter.MaySend(1000 + elapsed, 1000, true, interval));
        }

        [Fact]
        public void Timing_IntervalSettingClampsToSafeUiRange()
        {
            try
            {
                FlydigiVader5ProHidWriter.SetOutputIntervalMs(-1);
                Assert.Equal(10, FlydigiVader5ProHidWriter.OutputIntervalMs);
                FlydigiVader5ProHidWriter.SetOutputIntervalMs(100);
                Assert.Equal(60, FlydigiVader5ProHidWriter.OutputIntervalMs);
                FlydigiVader5ProHidWriter.SetOutputIntervalMs(30);
                Assert.Equal(30, FlydigiVader5ProHidWriter.OutputIntervalMs);
            }
            finally { FlydigiVader5ProHidWriter.SetOutputIntervalMs(30); }
        }

        [Fact]
        public void Scheduler_NonzeroFramesRetryAtExactAbsoluteDeadline()
        {
            var oldValue = Motors(180, 120, 30, 50);
            var next = Motors(181, 120, 30, 50);
            var driver = typeof(FlydigiVader5ProHidWriter);
            Assert.Equal(29, FlydigiVader5ProHidWriter.DelayUntilReport(
                oldValue, next, 1001, 1000, true, 30));
            Assert.Equal(1, FlydigiVader5ProHidWriter.DelayUntilReport(
                oldValue, next, 1029, 1000, true, 30));
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                oldValue, next, 1030, 1000, true, 30));
            // An early wake at 1029 must NOT restart a new 30ms timer.
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                oldValue, next, 1030, 1000, true, 30));
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                oldValue, next, 1000, 1000, false, 30));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Scheduler_EachIndividualMotorStopGetsPriority(int channel)
        {
            var wasOn = Motors(120, 120, 120, 120);
            var now = channel switch
            {
                0 => Motors(0, 120, 120, 120),
                1 => Motors(120, 0, 120, 120),
                2 => Motors(120, 120, 0, 120),
                _ => Motors(120, 120, 120, 0),
            };
            Assert.True(FlydigiVader5ProHidWriter.HasStoppingMotor(wasOn, now));
            Assert.Equal(3, FlydigiVader5ProHidWriter.DelayUntilReport(
                wasOn, now, 1000, 1000, true, 30));
            Assert.Equal(1, FlydigiVader5ProHidWriter.DelayUntilReport(
                wasOn, now, 1002, 1000, true, 30));
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                wasOn, now, 1003, 1000, true, 30));
        }

        [Fact]
        public void Scheduler_AllMotorsOffBypassNormalCadenceButDoNotSpam()
        {
            var wasOn = Motors(20, 40, 60, 80);
            Assert.True(FlydigiVader5ProHidWriter.HasStoppingMotor(wasOn, default));
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                wasOn, default, 1004, 1000, true, 30));
            Assert.Equal(-1, FlydigiVader5ProHidWriter.DelayUntilReport(
                default, default, 1200, 1004, true, 30));
            // Prevent a 1 kHz input poll from causing rapid ON reasserts.
            Assert.False(FlydigiVader5ProHidWriter.HasStoppingMotor(
                default, wasOn));
            Assert.Equal(28, FlydigiVader5ProHidWriter.DelayUntilReport(
                default, wasOn, 1002, 1000, true, 30));
            Assert.Equal(3, FlydigiVader5ProHidWriter.StopEdgeMinIntervalMs);
        }

        [Fact]
        public void Scheduler_SustainedCommandRetains500MsKeepAlive()
        {
            var on = Motors(200, 200, 0, 0);
            Assert.Equal(1, FlydigiVader5ProHidWriter.DelayUntilReport(
                on, on, 1499, 1000, true, 30));
            Assert.Equal(0, FlydigiVader5ProHidWriter.DelayUntilReport(
                on, on, 1500, 1000, true, 30));
            Assert.Equal(-1, FlydigiVader5ProHidWriter.DelayUntilReport(
                default, default, 1600, 1000, true, 30));
        }

        [Fact]
        public void NativeHapticEnvelope_StopsAfterShortSilenceWithoutChangingStockFollower()
        {
            // 20 ms of strong two-channel DualSense PCM followed by
            // 80 ms of complete silence. The 20ms mean-square follower
            // still has a perceptible tail; the Vader-only 8ms follower
            // must reach the 2% silence floor within that time.
            const int frames = 960; // 20ms at 48 kHz
            var strong = new byte[frames * 4];
            var silent = new byte[frames * 4];
            for (int i = 0; i < strong.Length; i += 4)
            {
                strong[i] = 0x00; strong[i + 1] = 0x70;
                strong[i + 2] = 0x00; strong[i + 3] = 0x70;
            }

            double normalLeft = 0, normalRight = 0;
            double fastLeft = 0, fastRight = 0;
            long normal = PersonaHapticRumble.Follow(
                ref normalLeft, ref normalRight, strong, 4, 0, 2, 48000);
            long fast = PersonaHapticRumble.Follow(
                ref fastLeft, ref fastRight, strong, 4, 0, 2, 48000,
                PersonaHapticRumble.LowLatencyTimeConstant);
            Assert.NotEqual(0L, normal);
            Assert.NotEqual(0L, fast);

            for (int n = 0; n < 4; n++)
            {
                normal = PersonaHapticRumble.Follow(
                    ref normalLeft, ref normalRight, silent, 4, 0, 2, 48000);
                fast = PersonaHapticRumble.Follow(
                    ref fastLeft, ref fastRight, silent, 4, 0, 2, 48000,
                    PersonaHapticRumble.LowLatencyTimeConstant);
            }
            Assert.Equal(0L, fast);
            Assert.NotEqual(0L, normal);
        }

        [Fact]
        public void NativeHaptics_StaleAfter50MsButOrdinaryHapticsKeep100Ms()
        {
            const long level = 123456789L;
            Assert.Equal(50, PersonaHapticRumble.LowLatencyStaleMs);
            Assert.Equal(level, PersonaHapticRumble.Fresh(
                level, 1000, 1050, PersonaHapticRumble.LowLatencyStaleMs));
            Assert.Equal(0L, PersonaHapticRumble.Fresh(
                level, 1000, 1051, PersonaHapticRumble.LowLatencyStaleMs));
            Assert.Equal(level, PersonaHapticRumble.Fresh(level, 1000, 1051));
            Assert.Equal(0L, PersonaHapticRumble.Fresh(level, 1000, 1101));
        }

        [Fact]
        public void NativeHaptics_UsesDedicatedFastFeedOnlyOnVader()
        {
            var audio = System.IO.File.ReadAllText(
                System.IO.Path.Combine(FindRepositoryRoot(), "PadForge.App",
                    "Common", "Input", "AudioPassthroughService.cs"));
            var input = System.IO.File.ReadAllText(
                System.IO.Path.Combine(FindRepositoryRoot(), "PadForge.App",
                    "Common", "Input", "InputManager.Step2.UpdateInputStates.cs"));
            var virtuals = System.IO.File.ReadAllText(
                System.IO.Path.Combine(FindRepositoryRoot(), "PadForge.App",
                    "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));
            Assert.Contains("HapticFastMeanSquareL", audio, StringComparison.Ordinal);
            Assert.Contains("HapticFastLevel", audio, StringComparison.Ordinal);
            Assert.Contains("PersonaHapticRumble.LowLatencyTimeConstant", audio, StringComparison.Ordinal);
            Assert.Contains("lowLatency: isVader", input, StringComparison.Ordinal);
            Assert.Contains("lowLatency: fastVaderMeter", input, StringComparison.Ordinal);
            Assert.Contains("bool fast = lowLatency && FlydigiVader5ProHidWriter.Enabled;",
                virtuals, StringComparison.Ordinal);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !System.IO.File.Exists(
                       System.IO.Path.Combine(directory.FullName, "PadForge.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return directory.FullName;
        }

        [Fact]
        public void AppSetting_IsOptInAndRoundTripsWithoutTouchingFlydigiEnhanced()
        {
            var initial = new AppSettingsData();
            Assert.False(initial.FlydigiVader5ProFourMotorEnabled);
            Assert.True(initial.FlydigiEnhancedProtocol);
            Assert.Equal(30, initial.FlydigiVader5ProOutputIntervalMs);
            initial.FlydigiVader5ProFourMotorEnabled = true;
            initial.FlydigiVader5ProOutputIntervalMs = 45;
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var text = new System.IO.StringWriter();
            serializer.Serialize(text, initial);
            using var source = new System.IO.StringReader(text.ToString());
            var restored = (AppSettingsData)serializer.Deserialize(source);
            Assert.True(restored.FlydigiVader5ProFourMotorEnabled);
            Assert.True(restored.FlydigiEnhancedProtocol);
            Assert.Equal(45, restored.FlydigiVader5ProOutputIntervalMs);
        }

        [Theory]
        [InlineData(0, 160, 0, 0, 0)]
        [InlineData(1, 0, 160, 0, 0)]
        [InlineData(2, 0, 0, 160, 0)]
        [InlineData(3, 0, 0, 0, 160)]
        public void TestButtons_IndependentlyTargetOneMotor(
            int motor, int gripLeft, int gripRight, int triggerLeft, int triggerRight)
        {
            var m = FlydigiVader5ProHidWriter.TestPattern(motor);
            Assert.Equal(gripLeft, m.GripLeft);
            Assert.Equal(gripRight, m.GripRight);
            Assert.Equal(triggerLeft, m.TriggerLeft);
            Assert.Equal(triggerRight, m.TriggerRight);
        }

        [Fact]
        public void TestPulse_RejectsInvalidMotorIndex()
        {
            Assert.False(FlydigiVader5ProHidWriter.PulseTestMotor(-1));
            Assert.False(FlydigiVader5ProHidWriter.PulseTestMotor(4));
            Assert.True(FlydigiVader5ProHidWriter.TestPattern(-1).Silent);
            Assert.True(FlydigiVader5ProHidWriter.TestPattern(4).Silent);
        }

        [Theory]
        [InlineData(0x37D7, 0x2401, false, true, true)]
        [InlineData(0x37D7, 0x2401, false, false, false)]
        [InlineData(0x37D7, 0x2401, true, false, true)]
        [InlineData(0x37D7, 0x2402, false, true, false)]
        [InlineData(0x045E, 0x2401, false, true, false)]
        [InlineData(0x045E, 0x02EA, true, false, true)]
        [InlineData(0x054C, 0x0CE6, false, false, false)]
        public void UiCapability_RecognizesNativeTriggerMotorsWithoutChangingOtherPads(
            int vid, int pid, bool sdlTriggers, bool nativeEnabled, bool expected)
        {
            Assert.Equal(expected, FlydigiVader5ProHidWriter.OffersImpulseTriggerOutput(
                (ushort)vid, (ushort)pid, sdlTriggers, nativeEnabled));
        }

        private static string ReadPadPageSource(string fileName)
        {
            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !System.IO.File.Exists(
                       System.IO.Path.Combine(directory.FullName, "PadForge.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return System.IO.File.ReadAllText(
                System.IO.Path.Combine(directory.FullName, "PadForge.App", "Views", fileName));
        }

        [Fact]
        public void UiForceFeedbackCard_ControlsIndependentTriggersUsingExistingSettings()
        {
            var xaml = ReadPadPageSource("PadPage.xaml");
            const string marker = "x:Name=\"FfbVaderFourMotorCard\"";
            int start = xaml.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start > 0);
            int end = xaml.IndexOf(
                "<Border x:Name=\"SteeringAngleRumbleCard\"", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string card = xaml.Substring(start, end - start);

            foreach (string setting in new[]
            {
                "ImpulseOverallGain", "ImpulseLeftStrength", "ImpulseRightStrength",
                "AtVibrationToImpulse", "TestLeftImpulseTriggerCommand",
                "TestRightImpulseTriggerCommand", "DeviceLeftTriggerMotorDisplay",
                "DeviceRightTriggerMotorDisplay",
            })
                Assert.Contains(setting, card, StringComparison.Ordinal);

            // The native card must not add new per-device gain fields:
            // it mirrors the regular Impulse Triggers tab's VM and XML values.
            Assert.Contains("Visibility=\"Collapsed\"", card, StringComparison.Ordinal);
            Assert.Contains("Pad_ImpulseTriggers_LeftTrigger", card, StringComparison.Ordinal);
            Assert.Contains("Pad_ImpulseTriggers_RightTrigger", card, StringComparison.Ordinal);
        }

        [Fact]
        public void UiSelectionAndToggle_RecomputePhysicalRumbleCapabilities()
        {
            var code = ReadPadPageSource("PadPage.xaml.cs");
            Assert.Contains("hasImpulseTriggers = FlydigiVader5ProHidWriter.OffersImpulseTriggerOutput(",
                code, StringComparison.Ordinal);
            Assert.Contains("FfbVaderFourMotorCard.Visibility = nativeVaderFourMotor",
                code, StringComparison.Ordinal);
            Assert.Contains("FlydigiVader5ProHidWriter.EnabledChanged += OnPeripheralOutputsChanged;",
                code, StringComparison.Ordinal);
            Assert.Contains("FlydigiVader5ProHidWriter.EnabledChanged -= OnPeripheralOutputsChanged;",
                code, StringComparison.Ordinal);
            Assert.Contains("SyncTabVisibility(); // native capabilities can change in Settings",
                code, StringComparison.Ordinal);
        }

        [Fact]
        public void ZeroReport_StopsEveryMotor()
        {
            var report = FlydigiVader5ProHidWriter.BuildReport(default);
            Assert.All(report.Skip(5).Take(4), b => Assert.Equal((byte)0, b));
            Assert.Equal((byte)(0x12 + 0x06), report[9]);
        }
    }
}
