using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The monochrome screens drawn without LGS (issue #519): the G15, G15
    /// v2, G510, G510s, G13, Z-10 and GamePanel, over HID, each keyboard its
    /// own screen.
    ///
    /// <para>A keyboard's collections are grouped by its USB IDs and the
    /// container ID Windows gives every collection of one device, the
    /// analog keyboards' grouping (<see cref="AnalogKeyboardHidRuntime.LinkSiblings"/>).
    /// A frame is one 992-byte output report, written with WriteFile on the
    /// keyboard's collection whose OutputReportByteLength is 992, through
    /// <see cref="RawHidOutput"/>, the wheel writers' path. A collection of
    /// any other length is never written. The keys come from the same
    /// keyboard's input reports, read on <see cref="VendorHidReader"/>'s
    /// threads and decoded by <see cref="KeyboardScreenFormats.DecodeMonoKeys"/>.
    /// While LGS draws, the writer stands down and the readers pass on the
    /// round applet key alone, which LGS never hands an applet. LGS hands the
    /// other four over itself while PadForge's applet is in front, and the
    /// host gives each one to the keyboard whose reader saw it go down.</para>
    ///
    /// <para>Runs on the host's worker. The scan opens a collection for its
    /// caps with no access asked, VendorHidRuntime's probe, and keeps each
    /// path's verdict until the path goes.</para>
    /// </summary>
    internal sealed class G15HidScreens : IDisposable
    {
        internal const int ScanMs = 3000;
        private const string KeysSource = "hid";

        /// <summary>A collection's key reader.</summary>
        internal interface IKeyReader : IDisposable
        {
            /// <summary>False once the device went quiet for good.</summary>
            bool IsAttached { get; }
        }

        /// <summary>One collection's caps, read through an open that asks
        /// no access.</summary>
        internal readonly record struct Caps(ushort OutputLength, ushort InputLength, ushort UsagePage, ushort Usage);

        /// <summary>The calls the screens make on the PC, so a test can
        /// stand in for each.</summary>
        internal sealed class Hid
        {
            /// <summary>Every present HID interface of a monochrome screen
            /// model, with its PID and Windows' container ID.</summary>
            public Func<List<(string Path, ushort Pid, string Container)>> Paths { get; init; }

            /// <summary>A collection's caps, null when it cannot be opened
            /// or read.</summary>
            public Func<string, Caps?> ReadCaps { get; init; }

            /// <summary>A reader on a key collection handing each report to
            /// the callback, null when it does not open.</summary>
            public Func<string, ushort, ushort, Action<byte[], int>, IKeyReader> OpenReader { get; init; }

            public Func<string, byte[], bool> Write { get; init; }
            public Action<string> Reset { get; init; }

            public static readonly Hid Real = new()
            {
                Paths = MonochromePaths,
                ReadCaps = ReadRealCaps,
                OpenReader = OpenRealReader,
                Write = RawHidOutput.Write,
                Reset = RawHidOutput.ResetDevice,
            };
        }

        private sealed class Collection
        {
            public string Path;
            public ushort ProductId;
            public bool Screen;
            public Keyboard Owner;
            public IKeyReader Reader;
            public long Written = -1;
        }

        /// <summary>One keyboard: its screen, its collections and what its
        /// readers last saw. The readers write <see cref="Raw"/> and
        /// <see cref="LastDown"/> on their threads, the worker reads
        /// them.</summary>
        private sealed class Keyboard
        {
            public KeyboardScreenInfo Info;
            public readonly List<Collection> Collections = new();
            public int Raw;
            public readonly long[] LastDown = new long[KeyboardScreenFormats.MonoKeyCount];
            public byte[] Report;
            public long ReportVersion = -1;
            public bool WroteFrame;
            public bool HasScreen => Collections.Exists(c => c.Screen);
        }

        private static readonly byte[] BlankReport =
            KeyboardScreenFormats.ToG15Report(new byte[KeyboardScreenFormats.MonoWidth * KeyboardScreenFormats.MonoHeight]);

        private readonly Dictionary<string, Collection> _collections = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Keyboard> _keyboards = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ushort> _inputLengths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Hid _io;
        private long _nextScan;
        private volatile bool _appletKeyOnly;

        public G15HidScreens(Hid io = null) => _io = io ?? Hid.Real;

        /// <summary>The keyboards whose screen collection is open, in id
        /// order.</summary>
        public IReadOnlyList<KeyboardScreenInfo> Screens { get; private set; } = Array.Empty<KeyboardScreenInfo>();

        /// <summary>LGS took the screens over: what PadForge wrote is gone
        /// under LGS's own drawing, so Off later clears nothing of
        /// LGS's.</summary>
        public void ForgetWrites()
        {
            foreach (var k in _keyboards.Values)
            {
                k.WroteFrame = false;
                k.ReportVersion = -1;
            }
            foreach (var c in _collections.Values) c.Written = -1;
        }

        /// <summary>One worker pass. <paramref name="frameOf"/> gives a
        /// screen's picture as B, G, R, A and its version, the picture null
        /// when PadForge shows nothing there, and <paramref name="draw"/> is
        /// false while LGS draws.</summary>
        public void Step(Func<string, (byte[] Frame, long Version)> frameOf, bool draw, bool appletKeyOnly, long now)
        {
            _appletKeyOnly = appletKeyOnly;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanMs;
                Scan();
            }
            if (!draw) return;

            foreach (var k in _keyboards.Values)
            {
                if (!k.HasScreen) continue;
                var (frame, version) = frameOf(k.Info.Id);
                if (frame == null)
                {
                    // Off without LGS: nothing else draws the screen, so the
                    // last picture PadForge wrote is cleared once.
                    if (k.WroteFrame)
                    {
                        k.Report = BlankReport;
                        k.ReportVersion = -2;
                        k.WroteFrame = false;
                    }
                    else if (k.ReportVersion != -2) continue;
                }
                else if (k.ReportVersion != version)
                {
                    k.Report = KeyboardScreenFormats.ToG15Report(KeyboardScreenFormats.ToLgsMono(frame));
                    k.ReportVersion = version;
                    k.WroteFrame = true;
                }
                if (k.Report == null) continue;

                foreach (var c in k.Collections)
                {
                    if (!c.Screen || c.Written == k.ReportVersion) continue;
                    if (_io.Write(c.Path, k.Report)) c.Written = k.ReportVersion;
                    else
                    {
                        // Gone or refused: the next scan finds it again or drops
                        // it, and its cached handle and length go now.
                        _io.Reset(c.Path);
                        _nextScan = 0;
                    }
                }
            }
        }

        /// <summary>Whether the screen's reader saw <paramref name="key"/>
        /// held, or go down within the last <paramref name="windowMs"/>.
        /// Worker thread.</summary>
        public bool KeyRecentlyDown(string screenId, int key, long now, long windowMs)
        {
            if (key < 0 || key >= KeyboardScreenFormats.MonoKeyCount) return false;
            if (!_keyboards.TryGetValue(screenId, out var k)) return false;
            if ((Volatile.Read(ref k.Raw) & (1 << key)) != 0) return true;
            long down = Volatile.Read(ref k.LastDown[key]);
            return down != 0 && now - down <= windowMs;
        }

        private void Scan()
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, pid, container) in _io.Paths())
            {
                present.Add(path);
                if (_collections.TryGetValue(path, out var known))
                {
                    // A reader whose device went quiet for good is opened
                    // again on this scan. The keyboard was unplugged, maybe
                    // plugged in again inside one scan's wait, so its screen
                    // lost the picture: every collection of it forgets what
                    // was written and its cached handle, and the next pass
                    // draws the picture again.
                    if (known.Reader != null && !known.Reader.IsAttached)
                    {
                        known.Reader.Dispose();
                        known.Reader = null;
                        if (known.Owner != null)
                        {
                            KeyboardScreenKeys.Clear(known.Owner.Info.Id, KeysSource);
                            foreach (var c in known.Owner.Collections)
                            {
                                c.Written = -1;
                                _io.Reset(c.Path);
                            }
                        }
                        OpenReader(known);
                    }
                    continue;
                }
                var info = KeyboardScreenInfo.Monochrome(pid, container, path);
                if (!_keyboards.TryGetValue(info.Id, out var owner))
                    owner = new Keyboard { Info = info };
                var probed = Probe(path, pid, owner, out bool unreadable);
                if (probed == null)
                {
                    // A collection that could not be read is asked again on
                    // the next scan. One that is no screen and carries no key
                    // is kept as a verdict.
                    if (!unreadable) _collections[path] = new Collection { Path = path, ProductId = pid };
                    continue;
                }
                _collections[path] = probed;
                owner.Collections.Add(probed);
                _keyboards[info.Id] = owner;
            }

            foreach (string gone in _collections.Keys.Where(p => !present.Contains(p)).ToList())
                Drop(gone);

            Screens = _keyboards.Values.Where(k => k.HasScreen).Select(k => k.Info)
                .OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        }

        private void Drop(string path)
        {
            if (!_collections.Remove(path, out var c)) return;
            c.Reader?.Dispose();
            _io.Reset(path);
            var owner = c.Owner;
            if (owner == null) return;
            owner.Collections.Remove(c);
            if (owner.Collections.Count > 0) return;
            _keyboards.Remove(owner.Info.Id);
            KeyboardScreenKeys.ClearScreen(owner.Info.Id);
        }

        /// <summary>The caps of one collection, read through an open that asks
        /// no access. Null for a collection that is neither the screen nor a
        /// key collection, and null with <paramref name="unreadable"/> set
        /// when it could not be opened or read.</summary>
        private Collection Probe(string path, ushort pid, Keyboard owner, out bool unreadable)
        {
            unreadable = true;
            try
            {
                if (_io.ReadCaps(path) is not Caps caps) return null;
                unreadable = false;
                bool screen = caps.OutputLength == KeyboardScreenFormats.G15ReportLength;
                // Windows keeps keyboards and mice to itself, so their
                // collections are not asked for reads.
                bool keys = caps.InputLength > 0
                            && !(caps.UsagePage == 0x01 && (caps.Usage == 0x06 || caps.Usage == 0x02));
                if (!screen && !keys) return null;
                var c = new Collection { Path = path, ProductId = pid, Screen = screen, Owner = owner };
                if (keys) OpenReader(c, caps.InputLength);
                return c;
            }
            catch (Exception)
            {
                unreadable = true;
                return null;
            }
        }

        /// <summary>The caps through an open that asks no access,
        /// VendorHidRuntime's probe, or null.</summary>
        private static Caps? ReadRealCaps(string path)
        {
            var handle = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS) return null;
                return new Caps(caps.OutputReportByteLength, caps.InputReportByteLength, caps.UsagePage, caps.Usage);
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
                handle.Dispose();
            }
        }

        private sealed class RealReader : IKeyReader
        {
            public VendorHidReader Reader;
            public bool IsAttached => Reader.IsAttached;
            public void Dispose() => Reader.Dispose();
        }

        private static IKeyReader OpenRealReader(string path, ushort pid, ushort inputLength, Action<byte[], int> onReport)
        {
            var reader = new VendorHidReader(new VendorHidCollection
            {
                Path = path,
                VendorId = KeyboardScreenDevices.LogitechVid,
                ProductId = pid,
                InputReportLength = inputLength,
            });
            reader.ReportReceived += (_, report, length) => onReport(report, length);
            if (reader.Open()) return new RealReader { Reader = reader };
            reader.Dispose();
            return null;
        }

        private void OpenReader(Collection c, ushort inputLength = 0)
        {
            if (c.Owner == null) return;
            if (inputLength > 0) _inputLengths[c.Path] = inputLength;
            else if (!_inputLengths.TryGetValue(c.Path, out inputLength)) return;
            ushort pid = c.ProductId;
            var owner = c.Owner;
            string screen = owner.Info.Id;
            c.Reader = _io.OpenReader(c.Path, pid, inputLength, (report, length) =>
            {
                int bits = KeyboardScreenFormats.DecodeMonoKeys(pid, report.AsSpan(0, length));
                if (bits < 0) return;
                int before = Interlocked.Exchange(ref owner.Raw, bits);
                int pressed = bits & ~before;
                if (pressed != 0)
                {
                    long now = Environment.TickCount64;
                    for (int b = 0; b < KeyboardScreenFormats.MonoKeyCount; b++)
                        if ((pressed & (1 << b)) != 0) Volatile.Write(ref owner.LastDown[b], now);
                }
                if (_appletKeyOnly) bits &= 1 << KeyboardScreenFormats.MonoAppletKey;
                KeyboardScreenKeys.Report(screen, KeysSource, bits);
            });
        }

        /// <summary>Every present HID interface of a monochrome screen
        /// model, by the VID and PID its path carries, with the container
        /// ID of its devnode.</summary>
        private static List<(string Path, ushort Pid, string Container)> MonochromePaths()
        {
            var list = new List<(string, ushort, string)>();
            SonyHeadsetHid.HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SonyHeadsetHid.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                SonyHeadsetHid.DIGCF_PRESENT | SonyHeadsetHid.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                var iface = new SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA>()
                };
                for (uint index = 0; SonyHeadsetHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface); index++)
                {
                    string path = AnalogKeyboardHidRuntime.GetInterfacePath(set, ref iface, out uint devInst);
                    if (TryReadIds(path, out ushort vid, out ushort pid) && KeyboardScreenDevices.IsMonochrome(vid, pid))
                        list.Add((path, pid, AnalogKeyboardHidRuntime.ContainerId(devInst)));
                }
            }
            finally
            {
                SonyHeadsetHid.SetupDiDestroyDeviceInfoList(set);
            }
            return list;
        }

        /// <summary>The VID and PID an interface path carries as
        /// <c>vid_046d&amp;pid_c22d</c>.</summary>
        internal static bool TryReadIds(string path, out ushort vid, out ushort pid)
        {
            vid = pid = 0;
            if (string.IsNullOrEmpty(path)) return false;
            int v = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
            int p = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            return v >= 0 && p >= 0 && v + 8 <= path.Length && p + 8 <= path.Length
                   && ushort.TryParse(path.AsSpan(v + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                   && ushort.TryParse(path.AsSpan(p + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }

        public void Dispose()
        {
            foreach (string path in _collections.Keys.ToList()) Drop(path);
            Screens = Array.Empty<KeyboardScreenInfo>();
        }
    }
}
