using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One program sending game-mod packets (#529), found by the UDP port it
    /// first sent from. The server leads every other port the same process
    /// owns to this sender.
    ///
    /// <para>DSX keeps a mod's effects until the mod sends
    /// ResetToUserSettings, and mods that crash or close never send it. So
    /// every value a mod holds is tied to the process that sent it, and it
    /// ends when that process ends. The process is the owner of the sender's
    /// UDP port, read from the owner-PID UDP tables (GetExtendedUdpTable),
    /// and a handle to it is kept so its id cannot be handed to another
    /// process while it is watched.</para>
    ///
    /// <para>A sender whose process cannot be found falls back to fifteen
    /// seconds after its last packet, the lapse DualSenseY uses for a
    /// driving app (source/udp.cpp:326) and the effects dispatcher's
    /// ExternalClaimLapseMs.</para>
    /// </summary>
    internal sealed class GameModSender : IDisposable
    {
        internal const long LapseMs = 15_000;

        /// <summary>The UDP port the first packet came from.</summary>
        internal readonly int Port;

        /// <summary>The sending process, or 0 when it could not be found.</summary>
        internal readonly int ProcessId;

        /// <summary>The sending program's file name, such as "GTA5.exe", or
        /// null when it could not be read.</summary>
        internal readonly string Name;

        private readonly SafeProcessHandle _process;
        private long _lastPacketMs;
        private int _disposed;

        internal GameModSender(int port, int processId, string name, SafeProcessHandle process, long nowMs)
        {
            Port = port;
            ProcessId = process != null ? processId : 0;
            Name = name;
            _process = process;
            _lastPacketMs = nowMs;
        }

        /// <summary>Finds the process that owns <paramref name="port"/> on
        /// this PC and opens it to watch for its exit.</summary>
        internal static GameModSender Identify(int port, long nowMs)
            => Identify(port, FindUdpOwner(port), nowMs);

        /// <summary>Opens process <paramref name="pid"/>, already found to
        /// own <paramref name="port"/>, to watch for its exit. A pid of 0,
        /// or a process that cannot be opened, makes a sender with no
        /// process.</summary>
        internal static GameModSender Identify(int port, int pid, long nowMs)
        {
            if (pid > 0)
            {
                var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, pid);
                if (handle != null && !handle.IsInvalid)
                    return new GameModSender(port, pid, ReadName(handle), handle, nowMs);
                handle?.Dispose();
            }
            return new GameModSender(port, 0, null, null, nowMs);
        }

        internal void Touch(long nowMs) => Volatile.Write(ref _lastPacketMs, nowMs);

        internal long LastPacketMs => Volatile.Read(ref _lastPacketMs);

        /// <summary>Whether this sender's values should end: its process
        /// exited, or, with no process to watch, nothing has arrived for
        /// <see cref="LapseMs"/>.</summary>
        internal bool IsGone(long nowMs)
        {
            if (Volatile.Read(ref _disposed) != 0) return true;
            if (_process != null)
            {
                try { return WaitForSingleObject(_process, 0) == WaitObject0; }
                catch (ObjectDisposedException) { return true; }
            }
            return nowMs - LastPacketMs >= LapseMs;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _process?.Dispose();
        }

        // ─────────────────────────────────────────────
        //  The UDP owner tables
        // ─────────────────────────────────────────────

        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int UdpTableOwnerPid = 1;
        private const uint ErrorInsufficientBuffer = 122;

        /// <summary>The process bound to <paramref name="port"/>, from the
        /// IPv4 table and then the IPv6 one (a dual-mode socket sending to
        /// 127.0.0.1 is listed there), or 0.</summary>
        internal static int FindUdpOwner(int port)
        {
            if (port <= 0 || port > 65535) return 0;
            int pid = FindInTable(port, AfInet, rowSize: 12, portOffset: 4, pidOffset: 8);
            if (pid == 0)
                pid = FindInTable(port, AfInet6, rowSize: 28, portOffset: 20, pidOffset: 24);
            return pid;
        }

        /// <summary>Reads one owner-PID table. MIB_UDPTABLE_OWNER_PID is a
        /// row count and then rows of address, port and PID, 12 bytes each.
        /// MIB_UDP6TABLE_OWNER_PID rows carry a 16-byte address and a scope
        /// id first, 28 bytes each. The port sits in network byte order in
        /// the low 16 bits of its field.</summary>
        private static int FindInTable(int port, int family, int rowSize, int portOffset, int pidOffset)
        {
            int size = 0;
            uint result = GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UdpTableOwnerPid, 0);
            if (result != ErrorInsufficientBuffer || size <= 0) return 0;
            // The table can grow between the two calls, so allow for a few rows more.
            size += rowSize * 16;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0);
                if (result != 0) return 0;
                int rows = Marshal.ReadInt32(buffer);
                for (int i = 0; i < rows; i++)
                {
                    int row = 4 + i * rowSize;
                    if (row + rowSize > size) break;
                    int raw = Marshal.ReadInt32(buffer, row + portOffset);
                    int rowPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    if (rowPort == port)
                        return Marshal.ReadInt32(buffer, row + pidOffset);
                }
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadName(SafeProcessHandle process)
        {
            var text = new StringBuilder(1024);
            int length = text.Capacity;
            if (!QueryFullProcessImageName(process, 0, text, ref length)) return null;
            try { return Path.GetFileName(text.ToString(0, length)); }
            catch (ArgumentException) { return null; }
        }

        private const int Synchronize = 0x00100000;
        private const int ProcessQueryLimitedInformation = 0x1000;
        private const uint WaitObject0 = 0;

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order,
            int family, int tableClass, uint reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
            StringBuilder name, ref int size);
    }
}
