using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>How a key read on the G19's interrupt pipe ended.</summary>
    internal enum G19Read { Ok, Timeout, Error }

    /// <summary>One open G19 screen interface: frames out, keys in. The
    /// handles are freed by Dispose.</summary>
    internal interface IG19Link : IDisposable
    {
        /// <summary>Whether interface 0 has the key pipe.</summary>
        bool HasKeys { get; }

        /// <summary>The key pipe's packet size.</summary>
        int KeyPacket { get; }

        /// <summary>Writes one whole frame. False with
        /// <paramref name="error"/> when it did not all go out.</summary>
        bool WriteFrame(byte[] data, out int error);

        G19Read ReadKeys(byte[] buffer, out int got);
    }

    /// <summary>The calls a G19 screen makes on the PC, so a test can stand
    /// in for each.</summary>
    internal sealed class G19Io
    {
        /// <summary>The path of PadForge's active WinUSB interface on the
        /// node, or null.</summary>
        public Func<string, string> FindPath { get; init; }

        /// <summary>Opens a path, null when it does not open.</summary>
        public Func<string, IG19Link> Open { get; init; }

        public Func<VendorUsbDriverInstaller.BindPlan, string, List<VendorUsbDriverInstaller.UsbNode>, Action<string>, CancellationToken, bool> Bind { get; init; }

        /// <summary>The provider the node's driver INF names, or null when
        /// it cannot be read.</summary>
        public Func<string, string> ProviderOf { get; init; }

        public static readonly G19Io Real = new()
        {
            ProviderOf = DriverProviderOf,
            FindPath = instanceId => VendorUsbDriverInstaller.ActiveInterfacePaths(VendorUsbDriverInstaller.InterfaceGuid)
                ?.FirstOrDefault(p => VendorUsbDriverInstaller.NamesNode(new[] { p }, instanceId)),
            Open = WinUsbG19Link.TryOpen,
            Bind = (plan, instanceId, nodes, log, ct) => VendorUsbDriverInstaller.Bind(plan, instanceId, nodes, log, ct),
        };

        /// <summary>A read of the node's driver provider, the way
        /// VendorUsbDriverInstaller.Restore reads it.</summary>
        private static string DriverProviderOf(string instanceId)
        {
            try
            {
                var dev = Nefarius.Utilities.DeviceManagement.PnP.PnPDevice.GetDeviceByInstanceId(instanceId,
                    Nefarius.Utilities.DeviceManagement.PnP.DeviceLocationFlags.Normal);
                return dev.GetProperty<string>(Nefarius.Utilities.DeviceManagement.PnP.DevicePropertyKey.Device_DriverProvider);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// One G19's color screen drawn without LGS (issue #519), over WinUSB on
    /// interface 0 of 046D:C229: frames to bulk OUT endpoint 0x02, the
    /// display keys from interrupt IN endpoint 0x81 (gnome15 pylibg19
    /// g19.py, G19USB G19Constants.cs). <see cref="G19UsbScreens"/> keeps
    /// one for each G19 plugged in, by the instance ID of its interface 0.
    ///
    /// <para>Interface 0 is bound to WinUSB through
    /// <see cref="VendorUsbDriverInstaller"/> and its rules: only from no
    /// driver or HidUsb, never from another vendor's driver, one INF naming
    /// the VID, PID and MI the way libwdi writes them. Interface 1, the
    /// G-keys, stays with Windows. The bind runs only while LGS does not draw
    /// the screens, and <see cref="Restore"/> hands the node back when the
    /// feature is switched off.</para>
    ///
    /// <para>The open and the pipe reads follow Ds3DirectService's USB path:
    /// an overlapped handle, WinUsb_Initialize, the pipes found by their
    /// type and direction, a transfer timeout on each, and synchronous calls
    /// on that handle (<see cref="WinUsbG19Link"/>). Frames are written on
    /// the host's worker and the keys read on a thread of their own.</para>
    /// </summary>
    internal sealed class G19UsbScreen : IDisposable
    {
        internal const ushort Vid = KeyboardScreenDevices.LogitechVid;
        internal const ushort Pid = KeyboardScreenDevices.G19;
        internal const int LookMs = 5000;
        internal const int BindRetryMs = 60000;
        /// <summary>Failed binds of one G19 before this screen stops trying,
        /// VendorUsbBindingService's MaxFailures: each bind writes the
        /// package and signs it again. The G19 plugged in again is a new
        /// screen and tries again.</summary>
        internal const int MaxBindFailures = 3;
        private const string KeysSource = "g19";

        /// <summary>Where interface 0 stands, for the status line.
        /// <see cref="State.OtherDriver"/> is a driver from Logitech, which
        /// LGS installs and serves while it runs.
        /// <see cref="State.ForeignDriver"/> is another program's (libusbK
        /// from Zadig, which G19USB's README has users install, libusb0, or
        /// someone else's WinUSB binding), which PadForge leaves alone and
        /// LGS cannot use.</summary>
        public enum State { None, Binding, OtherDriver, Open, ForeignDriver }

        /// <summary>One driver change on a G19 at a time: a bind and
        /// <see cref="Restore"/> each hold it for their whole run, so Restore
        /// sees the node as a bind in flight leaves it, and a bind that
        /// starts after the host stopped changes nothing.</summary>
        private static readonly object s_driverGate = new();

        private readonly Action<string> _log;
        private readonly string _instanceId;
        private readonly G19Io _io;
        private readonly CancellationToken _stopping;
        private volatile IG19Link _link;
        private Thread _reader;
        private volatile bool _open;
        /// <summary>The link whose key reader stopped on an error. A reader
        /// left running past Close's wait can stop after the screen opened
        /// again, so the flag names its own link, and only the open link's
        /// loss closes the screen.</summary>
        private volatile IG19Link _lostLink;
        private volatile bool _binding;
        private long _nextLook, _nextBind;
        /// <summary>Failed binds, written by the bind's task before it
        /// clears <see cref="_binding"/>, read by the worker after.</summary>
        private int _bindFailures;
        private string _providerService, _provider;
        private long _written = -1;

        public State Current { get; private set; } = State.None;

        /// <summary>The driver interface 0 sits on when it is another
        /// vendor's, for the status line.</summary>
        public string OtherDriver { get; private set; } = string.Empty;

        /// <param name="stopping">Canceled when the host stops: a bind not
        /// yet made is then never made.</param>
        public G19UsbScreen(string instanceId, Action<string> log, G19Io io = null, CancellationToken stopping = default)
        {
            _instanceId = instanceId;
            _log = log ?? (_ => { });
            _io = io ?? G19Io.Real;
            _stopping = stopping;
            Info = KeyboardScreenInfo.G19(instanceId);
        }

        /// <summary>The screen this G19 is.</summary>
        public KeyboardScreenInfo Info { get; }

        /// <summary>The bind started last, for the tests.</summary>
        internal Task BindTask { get; private set; }

        /// <summary>One worker pass. <paramref name="mayBind"/> is false while
        /// LGS draws the screens. A screen already on PadForge's binding is
        /// drawn either way: LGS cannot reach it then.</summary>
        public void Step(byte[] frame, long version, bool mayBind, long now, List<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            // The key reader stopped on an error: the G19 was unplugged or
            // stopped, maybe inside one look's wait and plugged in again.
            // The screen closes, the next look opens it again, and the open
            // has nothing written, so the picture goes out again.
            if (_open && _lostLink != null && ReferenceEquals(_lostLink, _link)) Close();
            if (!_open)
            {
                if (_binding || now < _nextLook) return;
                _nextLook = now + LookMs;
                Look(mayBind, now, nodes);
                if (!_open) return;
            }

            byte[] bgra = frame;
            long wanted = version;
            if (bgra == null)
            {
                // Off: nothing else draws a screen PadForge holds, so the
                // last picture is cleared to black once.
                if (_written < 0) return;
                bgra = new byte[KeyboardScreenFormats.ColorWidth * KeyboardScreenFormats.ColorHeight * 4];
                wanted = -2;
            }
            if (_written == wanted) return;
            var data = KeyboardScreenFormats.ToG19Frame(bgra);
            if (_link.WriteFrame(data, out int error))
                _written = wanted;
            else
            {
                _log("G19 screen: a frame write failed (" + error + "), closing the screen");
                Close();
            }
        }

        private void Look(bool mayBind, long now, List<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            var node = nodes?.FirstOrDefault(n => string.Equals(n.InstanceId, _instanceId, StringComparison.OrdinalIgnoreCase)) ?? default;
            if (string.IsNullOrEmpty(node.InstanceId))
            {
                Current = State.None;
                return;
            }
            string service = node.Service ?? string.Empty;
            if (service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
            {
                string path = _io.FindPath(node.InstanceId);
                if (path != null && Open(path))
                {
                    Current = State.Open;
                    return;
                }
                // WinUSB with no interface of PadForge's: another program's
                // binding, which is not PadForge's to take.
                Current = path == null ? DriverState(node.InstanceId, service) : State.Binding;
                OtherDriver = service;
                return;
            }
            if (service.Length != 0 && !service.Equals("HidUsb", StringComparison.OrdinalIgnoreCase))
            {
                Current = DriverState(node.InstanceId, service);
                OtherDriver = service;
                return;
            }
            Current = State.Binding;
            if (!mayBind || now < _nextBind || _bindFailures >= MaxBindFailures) return;
            _nextBind = now + BindRetryMs;
            _binding = true;
            var plan = new VendorUsbDriverInstaller.BindPlan(VendorUsbDriverInstaller.BindId(node), "Logitech G19 screen");
            // The bind signs, stocks and forces a package, which takes
            // seconds, so it runs off the worker and the other screens keep
            // drawing. It waits for a Restore in flight, and the host may
            // have stopped meanwhile (the switch went off, the engine
            // stopped), in which case it binds nothing.
            BindTask = Task.Run(() =>
            {
                try
                {
                    lock (s_driverGate)
                    {
                        if (_stopping.IsCancellationRequested) return;
                        if (_io.Bind(plan, node.InstanceId, nodes, _log, _stopping)) _bindFailures = 0;
                        else Failed();
                    }
                }
                catch (Exception ex)
                {
                    _log("G19 screen: bind failed: " + ex.Message);
                    Failed();
                }
                finally
                {
                    _nextLook = 0;
                    _binding = false;
                }
            });
        }

        /// <summary>Whose driver interface 0 is on, by the provider its INF
        /// names (DEVPKEY_Device_DriverProvider, the property
        /// VendorUsbDriverInstaller.Restore reads), read again only when the
        /// service changes. A provider that cannot be read keeps the LGS
        /// line.</summary>
        private State DriverState(string instanceId, string service)
        {
            if (!string.Equals(service, _providerService, StringComparison.OrdinalIgnoreCase))
            {
                _providerService = service;
                _provider = _io.ProviderOf?.Invoke(instanceId);
            }
            return _provider == null || _provider.Contains("Logitech", StringComparison.OrdinalIgnoreCase)
                ? State.OtherDriver
                : State.ForeignDriver;
        }

        private void Failed()
        {
            if (++_bindFailures == MaxBindFailures)
                _log($"G19 screen: {MaxBindFailures} binds of {_instanceId} failed, so this screen stops trying until the G19 is plugged in again.");
        }

        private bool Open(string path)
        {
            // The test host opens no device (DirectDeviceAccess, asked by
            // the real opener).
            var link = _io.Open(path);
            if (link == null) return false;
            _link = link;
            _written = -1;
            _lostLink = null;
            _open = true;
            if (link.HasKeys)
            {
                _reader = new Thread(ReadKeys) { IsBackground = true, Name = "PadForge.G19Keys" };
                _reader.Start();
            }
            _log("G19 screen: open on " + path);
            return true;
        }

        private void ReadKeys()
        {
            var link = _link;
            var buffer = new byte[Math.Max(link.KeyPacket, 2)];
            // A reader serves its own link only: one left running past
            // Close's wait stops when the screen opens again on another.
            while (_open && ReferenceEquals(_link, link))
            {
                var read = link.ReadKeys(buffer, out int got);
                if (!ReferenceEquals(_link, link)) break;
                if (read == G19Read.Ok)
                {
                    int bits = KeyboardScreenFormats.DecodeG19Keys(buffer.AsSpan(0, Math.Clamp(got, 0, buffer.Length)));
                    if (bits >= 0) KeyboardScreenKeys.Report(Info.Id, KeysSource, bits);
                    continue;
                }
                if (read == G19Read.Timeout) continue;
                // Unplugged or stopped: the worker's next pass closes the
                // screen, and the look after it opens the screen again.
                _lostLink = link;
                break;
            }
            if (ReferenceEquals(_link, link)) KeyboardScreenKeys.Clear(Info.Id, KeysSource);
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            var reader = _reader;
            _reader = null;
            // The reader wakes within its 100 ms timeout. One still inside a
            // native call keeps the handles, which are left rather than freed
            // under it (the headset reader's rule).
            if (reader != null && !reader.Join(1000)) return;
            _link.Dispose();
            _link = null;
            _lostLink = null;
            _nextLook = 0;
            Current = State.None;
            KeyboardScreenKeys.Clear(Info.Id, KeysSource);
        }

        public void Dispose() => Close();

        /// <summary>Gives every G19's interface 0 that is on PadForge's
        /// WinUSB package back to Windows, for the feature switched off.
        /// Blocks for seconds, waiting first for a bind in flight, so the
        /// caller runs it off the UI thread.</summary>
        public static void Restore(Action<string> log)
            => Restore(log, VendorUsbDriverInstaller.ListPresentUsbNodes, VendorUsbDriverInstaller.HasActiveInterface,
                (instanceId, l) => VendorUsbDriverInstaller.Restore(instanceId, l, CancellationToken.None));

        /// <summary>Test seam: the node list, the PadForge interface check
        /// and the restore of one node.</summary>
        internal static void Restore(Action<string> log, Func<List<VendorUsbDriverInstaller.UsbNode>> listNodes,
            Func<string, bool> hasActiveInterface, Func<string, Action<string>, bool> restore)
        {
            lock (s_driverGate)
            {
                var nodes = listNodes();
                if (nodes == null) return;
                foreach (var node in nodes)
                {
                    if (node.Vid != Vid || node.Pid != Pid || node.Interface != 0 || string.IsNullOrEmpty(node.InstanceId)) continue;
                    if (!(node.Service ?? string.Empty).Equals("WINUSB", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!hasActiveInterface(node.InstanceId)) continue;
                    restore(node.InstanceId, log ?? (_ => { }));
                }
            }
        }
    }

    /// <summary>
    /// A G19's interface 0 opened over WinUSB, Ds3DirectService's USB path:
    /// an overlapped handle, WinUsb_Initialize, the pipes found by their
    /// type and direction and a transfer timeout on each.
    /// </summary>
    internal sealed class WinUsbG19Link : IG19Link
    {
        private IntPtr _dev;
        private IntPtr _ifh;
        private readonly byte _outPipe, _inPipe;

        private WinUsbG19Link(IntPtr dev, IntPtr ifh, byte outPipe, byte inPipe, ushort inPacket)
        {
            _dev = dev;
            _ifh = ifh;
            _outPipe = outPipe;
            _inPipe = inPipe;
            KeyPacket = inPacket;
        }

        public bool HasKeys => _inPipe != 0;
        public int KeyPacket { get; }

        public static IG19Link TryOpen(string path)
        {
            // The test host opens no device (DirectDeviceAccess).
            if (DirectDeviceAccess.Refuses(path)) return null;
            IntPtr dev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (dev == InvalidHandle) return null;
            if (!WinUsb_Initialize(dev, out IntPtr ifh))
            {
                CloseHandle(dev);
                return null;
            }
            byte outPipe = 0, inPipe = 0;
            ushort inPacket = 0;
            if (WinUsb_QueryInterfaceSettings(ifh, 0, out var idesc))
            {
                for (byte i = 0; i < idesc.bNumEndpoints; i++)
                {
                    if (!WinUsb_QueryPipe(ifh, 0, i, out var pipe)) continue;
                    if (pipe.PipeId == 0x02 && pipe.PipeType == 2 /* bulk */) outPipe = pipe.PipeId;
                    else if (pipe.PipeId == 0x81 && pipe.PipeType == 3 /* interrupt */)
                    {
                        inPipe = pipe.PipeId;
                        inPacket = pipe.MaximumPacketSize;
                    }
                }
            }
            if (outPipe == 0)
            {
                WinUsb_Free(ifh);
                CloseHandle(dev);
                return null;
            }
            // A frame is one bulk write, gnome15's and G19USB's 1000 ms each,
            // and a short read timeout wakes the key reader to check that the
            // screen is still open.
            uint writeTimeout = 1000, readTimeout = 100;
            WinUsb_SetPipePolicy(ifh, outPipe, PIPE_TRANSFER_TIMEOUT, 4, ref writeTimeout);
            if (inPipe != 0) WinUsb_SetPipePolicy(ifh, inPipe, PIPE_TRANSFER_TIMEOUT, 4, ref readTimeout);
            return new WinUsbG19Link(dev, ifh, outPipe, inPipe, Math.Max(inPacket, (ushort)2));
        }

        public bool WriteFrame(byte[] data, out int error)
        {
            error = 0;
            if (WinUsb_WritePipe(_ifh, _outPipe, data, (uint)data.Length, out uint moved, IntPtr.Zero) && moved == data.Length)
                return true;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        public G19Read ReadKeys(byte[] buffer, out int got)
        {
            got = 0;
            if (WinUsb_ReadPipe(_ifh, _inPipe, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
            {
                got = (int)Math.Min(read, (uint)buffer.Length);
                return G19Read.Ok;
            }
            return Marshal.GetLastWin32Error() == ERROR_SEM_TIMEOUT ? G19Read.Timeout : G19Read.Error;
        }

        public void Dispose()
        {
            if (_ifh != IntPtr.Zero) WinUsb_Free(_ifh);
            if (_dev != InvalidHandle && _dev != IntPtr.Zero) CloseHandle(_dev);
            _ifh = IntPtr.Zero;
            _dev = InvalidHandle;
        }

        // ── WinUSB interop, Ds3DirectService's declarations ─────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct USB_INTERFACE_DESCRIPTOR { public byte bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINUSB_PIPE_INFORMATION { public int PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }

        private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_SEM_TIMEOUT = 121;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(IntPtr dev, out IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr ifh, byte pipeId, byte[] buf, uint len, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr ifh, byte pipeId, byte[] buf, uint len, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr ifh, byte alt, out USB_INTERFACE_DESCRIPTOR desc);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr ifh, byte alt, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipe);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr ifh, byte pipeId, uint policyType, uint valueLen, ref uint value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    }

    /// <summary>
    /// Every G19 plugged in (issue #519), a <see cref="G19UsbScreen"/> each,
    /// found by the instance ID of its interface 0 on a look every
    /// <see cref="G19UsbScreen.LookMs"/>. A G19 that leaves takes its screen
    /// and its keys with it. Runs on the host's worker.
    /// </summary>
    internal sealed class G19UsbScreens : IDisposable
    {
        private readonly Action<string> _log;
        private readonly Dictionary<string, G19UsbScreen> _screens = new(StringComparer.OrdinalIgnoreCase);
        private List<VendorUsbDriverInstaller.UsbNode> _nodes = new();
        private long _nextLook;
        private readonly CancellationToken _stopping;

        /// <param name="stopping">The host's stop, handed to each screen's
        /// bind.</param>
        public G19UsbScreens(Action<string> log, CancellationToken stopping = default)
        {
            _log = log ?? (_ => { });
            _stopping = stopping;
        }

        /// <summary>The G19s plugged in, in id order.</summary>
        public IReadOnlyList<KeyboardScreenInfo> Screens { get; private set; } = Array.Empty<KeyboardScreenInfo>();

        /// <summary>The G19 a screen id names, or null.</summary>
        public G19UsbScreen Find(string id)
        {
            foreach (var screen in _screens.Values)
                if (string.Equals(screen.Info.Id, id, StringComparison.Ordinal)) return screen;
            return null;
        }

        /// <summary>One worker pass. <paramref name="mayBind"/> is false while
        /// LGS draws the screens.</summary>
        public void Step(Func<string, (byte[] Frame, long Version)> frameOf, bool mayBind, long now)
        {
            if (now >= _nextLook)
            {
                _nextLook = now + G19UsbScreen.LookMs;
                _nodes = VendorUsbDriverInstaller.ListPresentUsbNodes() ?? new List<VendorUsbDriverInstaller.UsbNode>();
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in _nodes)
                {
                    if (node.Vid != G19UsbScreen.Vid || node.Pid != G19UsbScreen.Pid || node.Interface != 0
                        || string.IsNullOrEmpty(node.InstanceId)) continue;
                    present.Add(node.InstanceId);
                    if (!_screens.ContainsKey(node.InstanceId))
                        _screens[node.InstanceId] = new G19UsbScreen(node.InstanceId, _log, stopping: _stopping);
                }
                foreach (string gone in _screens.Keys.Where(id => !present.Contains(id)).ToList())
                {
                    var screen = _screens[gone];
                    _screens.Remove(gone);
                    screen.Dispose();
                    KeyboardScreenKeys.ClearScreen(screen.Info.Id);
                }
                Screens = _screens.Values.Select(s => s.Info).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            }
            foreach (var screen in _screens.Values)
            {
                var (frame, version) = frameOf(screen.Info.Id);
                try { screen.Step(frame, version, mayBind, now, _nodes); }
                catch (Exception ex) { _log("G19 screen: pass failed: " + ex.Message); }
            }
        }

        public void Dispose()
        {
            foreach (var screen in _screens.Values)
            {
                screen.Dispose();
                KeyboardScreenKeys.ClearScreen(screen.Info.Id);
            }
            _screens.Clear();
            Screens = Array.Empty<KeyboardScreenInfo>();
        }
    }
}
