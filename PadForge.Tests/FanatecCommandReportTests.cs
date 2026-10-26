using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Fanatec wheel commands went out as report 0x00 through 5.1.0, and a
    /// CSL DD got no force feedback at all. Fanatec bases number their output
    /// reports. hid-fanatecff sends every command through the first output
    /// report the descriptor declares (ftec_init), Linux writes that report's
    /// ID ahead of the payload (hid_output_report), and the driver passes the
    /// Fanatec SDK's reports 1, 2 and 255 straight to the device. A write that
    /// names report 0 on numbered reports fails, so force, springs, centering,
    /// rotation range and rev LEDs never reached the base. The pedal report
    /// already used 0x01.
    /// </summary>
    public class FanatecCommandReportTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void WheelCommands_GoOutOnTheCommandReport()
        {
            // The rotation range command for 900 degrees (f8 81 lo hi).
            byte[] cmd = { 0xf8, 0x81, 0x84, 0x03, 0x00, 0x00, 0x00 };
            byte[] report = FanatecRawHidWriter.BuildWheelReport(cmd);
            Assert.Equal(8, report.Length);
            Assert.Equal(0x01, report[0]);
            Assert.Equal(cmd, report[1..]);
        }

        [Fact]
        public void WheelAndPedalReports_ShareTheCommandReport()
        {
            Assert.Equal(0x01, FanatecRawHidWriter.CommandReportId);
            Assert.Equal(FanatecRawHidWriter.CommandReportId, FanatecRawHidWriter.BuildPedalReport(0, 0)[0]);
            Assert.Equal(FanatecRawHidWriter.CommandReportId, FanatecRawHidWriter.BuildWheelReport(new byte[7])[0]);
        }

        /// <summary>Every write in the Fanatec writer sends a report from one of
        /// the two builders, so no command can frame its own report ID.</summary>
        [Fact]
        public void EveryFanatecWrite_UsesAReportBuilder()
        {
            string source = File.ReadAllText(Path.Combine(Root(),
                "PadForge.App", "Common", "Input", "FanatecRawHidWriter.cs"));
            var writes = Regex.Matches(source, @"RawHidOutput\.Write\w*\(\s*devicePath\s*,\s*([^;]*)");
            Assert.True(writes.Count >= 8, writes.Count.ToString());
            foreach (Match w in writes)
            {
                string payload = w.Groups[1].Value.TrimStart();
                Assert.True(payload.StartsWith("BuildWheelReport(", StringComparison.Ordinal)
                    || payload.StartsWith("BuildPedalReport(", StringComparison.Ordinal), payload);
            }
            Assert.DoesNotMatch(@"report\[0\]\s*=\s*0x00", source);
        }
    }
}
