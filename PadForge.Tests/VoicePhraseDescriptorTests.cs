using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    // Locks the voice-phrase descriptor family (issue #317): "Any Voice
    // Phrase" and "Voice Phrase N" resolve against the raw buttons
    // VoicePulse stamps at VoicePhraseButtonBase + N on a
    // microphone-bearing pad. This is the surface a macro trigger /
    // mapping source binds, so a spoken phrase fires the bound action.
    // Mirrors NfcTagDescriptorTests, the family this grammar is copied
    // from.
    [Collection("SettingsManagerStatics")]
    public class VoicePhraseDescriptorTests
    {
        private static MappingSource Src(string descriptor)
            => new MappingSource { Descriptor = descriptor, DeviceGuid = "" };

        private static CustomInputState StateWithPulses(params int[] held)
        {
            var s = new CustomInputState();
            foreach (int b in held)
                s.Buttons[SourceCoercion.VoicePhraseButtonBase + b] = true;
            return s;
        }

        [Fact]
        public void AnyVoicePhrase_FiresWhenSlotZeroPulsed()
        {
            Assert.True(SourceCoercion.IsVoicePhraseDescriptor("Any Voice Phrase"));
            var held = StateWithPulses(0);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(held, Src("Any Voice Phrase"), 50, 0));
            var idle = new CustomInputState();
            Assert.False(SourceCoercion.EvaluateForButtonTarget(idle, Src("Any Voice Phrase"), 50, 0));
        }

        [Fact]
        public void NumberedPhrase_FiresOnlyForItsOwnButton()
        {
            var held = StateWithPulses(0, 3); // any + phrase button 3
            Assert.True(SourceCoercion.EvaluateForButtonTarget(held, Src("Voice Phrase 3"), 50, 0));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(held, Src("Voice Phrase 2"), 50, 0));
        }

        [Fact]
        public void PulseButtons_DoNotCollideWithPhysicalRange()
        {
            // A pad holding every physical/extended button below the base
            // must not read as a phrase.
            var s = new CustomInputState();
            for (int i = 0; i < SourceCoercion.VoicePhraseButtonBase; i++)
                s.Buttons[i] = true;
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, Src("Any Voice Phrase"), 50, 0));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, Src("Voice Phrase 1"), 50, 0));
        }

        [Fact]
        public void DescriptorRoundTrip_ButtonToDescriptorAndBack()
        {
            Assert.Equal("Any Voice Phrase", SourceCoercion.VoicePhraseDescriptorForButton(0));
            Assert.Equal("Voice Phrase 7", SourceCoercion.VoicePhraseDescriptorForButton(7));
            Assert.True(SourceCoercion.TryGetVoicePhraseButton("Voice Phrase 7", out int b));
            Assert.Equal(7, b);
            Assert.True(SourceCoercion.TryGetVoicePhraseButton("Any Voice Phrase", out int any));
            Assert.Equal(0, any);
        }

        [Fact]
        public void NonVoiceDescriptors_AreNotClaimed()
        {
            Assert.False(SourceCoercion.IsVoicePhraseDescriptor("Button 3"));
            Assert.False(SourceCoercion.IsVoicePhraseDescriptor("Any NFC Tag"));
            Assert.False(SourceCoercion.IsVoicePhraseDescriptor("Voice Phrase 0"));  // 0 is the Any form, not numbered
            Assert.False(SourceCoercion.IsVoicePhraseDescriptor("Voice Phrase 56")); // beyond the 256-button state
            Assert.Equal(SourceCoercion.SourceType.VoicePhrase,
                SourceCoercion.ClassifyDescriptor("Voice Phrase 1"));
        }

        [Fact]
        public void FiresAsTriggerAndAxisContribution()
        {
            var held = StateWithPulses(0);
            // Trigger-pull read: 0/1.
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(held, Src("Any Voice Phrase"), 0), 3);
            // Bipolar-axis contribution: 0/1.
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(held, Src("Any Voice Phrase"), 0), 3);
        }

        /// <summary>The param/gate readers must see voice descriptors, the
        /// same contract the NFC family carries (#248 audit finding 4):
        /// the pickers offer them for Incremental/Ramped ParamUp/Down and
        /// Invert-on-Hold modifiers, and standalone-microphone phrases work
        /// there as raw buttons, so pad phrases must read identically.</summary>
        [Fact]
        public void VoiceDescriptor_DrivesIncrementalParamUp()
        {
            var rt = new PadForge.Engine.Common.Mapping.SourceKindRuntime();
            var src = new PadForge.Engine.Data.MappingSource
            {
                Kind = "Incremental",
                Descriptor = "Button 0",
                ParamUp = "Any Voice Phrase",
                ParamRate = 1.0,
                ParamMin = 0,
                ParamMax = 1,
            };
            var held = StateWithPulses(0);
            double v = 0;
            for (int i = 0; i < 10; i++)
            {
                rt.FrameSeq++;
                v = rt.TickIncremental(0, "LeftTrigger", 0, src, held, 0.05);
            }
            Assert.True(v > 0.4, $"phrase-held ParamUp never ramped (v={v})");

            var idle = new CustomInputState();
            rt.FrameSeq++;
            double after = rt.TickIncremental(0, "LeftTrigger", 0, src, idle, 0.05);
            Assert.Equal(v, after, 3);
        }

        [Fact]
        public void HardwareBoolDescriptor_ReadsVoiceAndRejectsOthers()
        {
            var held = StateWithPulses(0, 2);
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(held, "Any Voice Phrase"));
            Assert.True(SourceCoercion.ReadHardwareBoolDescriptor(held, "Voice Phrase 2"));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(held, "Voice Phrase 1"));
            Assert.False(SourceCoercion.ReadHardwareBoolDescriptor(null, "Any Voice Phrase"));
        }

        private const string Ds4BtPath = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&09cc";
        private const string Ds4V1BtPath = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&05c4";
        private const string Ds4UsbPath = @"\\?\HID#VID_054C&PID_09CC&MI_03#7&1f882a25&0&0000";

        /// <summary>The embedded-microphone gate: the DualSense family
        /// carries phrases on the pad's own surface, and so does a DualShock 4
        /// on Bluetooth, through the headset in its jack (#528). Everything
        /// else gets them from a standalone microphone device row instead.</summary>
        [Theory]
        [InlineData(0x054C, 0x0CE6, "", true)]           // DualSense
        [InlineData(0x054C, 0x0DF2, "", true)]           // DualSense Edge
        [InlineData(0x054C, 0x09CC, Ds4BtPath, true)]    // DualShock 4 on Bluetooth
        [InlineData(0x054C, 0x05C4, Ds4V1BtPath, true)]  // first DualShock 4 on Bluetooth
        [InlineData(0x054C, 0x09CC, Ds4UsbPath, false)]  // DualShock 4 on a cable: PadForge reads no microphone there
        [InlineData(0x054C, 0x09CC, "", false)]          // no path yet
        [InlineData(0x057E, 0x2009, "", false)]          // Switch Pro: no microphone
        [InlineData(0x045E, 0x0CE6, "", false)]          // wrong vendor, right PID
        public void HasVoicePhrases_GatesByExactHardware(int vid, int pid, string path, bool expected)
        {
            var ud = new PadForge.Engine.Data.UserDevice
            { VendorId = (ushort)vid, ProdId = (ushort)pid, DevicePath = path };
            Assert.Equal(expected, ud.HasVoicePhrases);
        }

        /// <summary>With the DualShock 4 microphone lane off, no DualShock 4
        /// carries phrases, matching IsPadWithEmbeddedMic and the Manage
        /// Voice Macros button. The DualSense keeps them.</summary>
        [Fact]
        public void HasVoicePhrases_FollowsTheDualShock4MicrophoneSwitch()
        {
            var previous = UserDevice.Ds4BtMicEnabled;
            try
            {
                UserDevice.Ds4BtMicEnabled = () => false;
                Assert.False(new UserDevice { VendorId = 0x054C, ProdId = 0x09CC, DevicePath = Ds4BtPath }.HasVoicePhrases);
                Assert.False(new UserDevice { VendorId = 0x054C, ProdId = 0x05C4, DevicePath = Ds4V1BtPath }.HasVoicePhrases);
                Assert.True(new UserDevice { VendorId = 0x054C, ProdId = 0x0CE6 }.HasVoicePhrases);
                UserDevice.Ds4BtMicEnabled = () => true;
                Assert.True(new UserDevice { VendorId = 0x054C, ProdId = 0x09CC, DevicePath = Ds4BtPath }.HasVoicePhrases);
            }
            finally
            {
                UserDevice.Ds4BtMicEnabled = previous;
            }

            // The App answers with its own switch.
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("PadForge.Engine.Data.UserDevice.Ds4BtMicEnabled = () => PadForge.Common.Input.AudioPassthroughService.EnableDs4BtMic;", src);
        }

        /// <summary>The Manage Voice Macros button follows the same pads: a
        /// DualShock 4 shows it on Bluetooth while its microphone lane is on,
        /// as the DualSense shows it on Bluetooth (#528).</summary>
        [Fact]
        public void ManageVoiceMacros_ShowsForABluetoothDualShock4()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                dir.FullName, "PadForge.App", "ViewModels", "DeviceRowViewModel.cs"));
            int at = src.IndexOf("public bool ShowManageVoicePhrases", System.StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, 700);
            Assert.Contains("ProductId == 0x05C4 || ProductId == 0x09CC", body);
            Assert.Contains("IsBluetoothLink && PadForge.Common.Input.AudioPassthroughService.EnableDs4BtMic", body);
        }
    }
}
