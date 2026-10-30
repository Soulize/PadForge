using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Experimental, opt-in four-motor output for Flydigi Vader 5 Pro
    /// (37D7:2401). Keeps the SDL input path intact while sending the
    /// four 8-bit motor levels through the separate vendor HID collection.
    /// The packet is the 0x12 NewXInput command documented and hardware-tested
    /// by OpenVader (MIT), with Windows report ID 0x06.
    ///
    /// Only ONE matching physical controller is supported. On ambiguous
    /// enumeration or two competing SDL owners the writer refuses output
    /// rather than vibrating the wrong controller.
    /// </summary>
    internal static class FlydigiVader5ProHidWriter
    {
        internal const ushort VendorId = 0x37D7;
        internal const ushort ProductId = 0x2401;
        internal const ushort ControlUsagePage = 0xFFA0;
        internal const ushort ControlUsage = 1;
        internal const int MinimumWriteIntervalMs = 30;
        internal const int RetryIntervalMs = 1000;
        // Old 250 ms watchdog was noticeable as a lingering vibration.
        // With a normally-running 1 kHz PadForge poll, 100 ms allows
        // plenty of missed polls but zeroes a stalled source promptly.
        internal const int StaleInputMs = 100;
        internal const int StopEdgeMinIntervalMs = 3;
        internal const int MaxIdleWaitMs = 100;
        internal const int KeepAliveMs = 500;

        internal readonly record struct Motors(byte GripLeft, byte GripRight,
            byte TriggerLeft, byte TriggerRight)
        {
            internal bool Silent => (GripLeft | GripRight | TriggerLeft | TriggerRight) == 0;
        }

        private static readonly object StateGate = new();
        private static readonly object IoGate = new();
        private static readonly AutoResetEvent Wake = new(false);
        private static Thread _worker;
        private static int _enabled;
        private static int _outputIntervalMs = MinimumWriteIntervalMs;
        private static Motors _desired;
        private static Motors _sent;
        private static Motors _testMotors;
        private static long _testUntilAt;
        private static bool _everSent;
        private static Guid _owner;
        private static bool _ambiguousOwner;
        private static long _lastInputAt;
        private static long _lastWriteAt;
        private static long _nextScanAt;
        private static string _controlPath;
        private static string _pendingResetPath;
        private static string _status = "Disabled";

        internal static event Action StatusChanged;
        // The Pad page's hardware-capability tabs update immediately when
        // the opt-in backend changes, without needing a device re-enumeration.
        internal static event Action EnabledChanged;
        internal static string Status => Volatile.Read(ref _status);

        private static void SetStatus(string status)
        {
            if (Interlocked.Exchange(ref _status, status) == status) return;
            try { PadForge.Engine.SdlDiagLog.WriteLine("FLYDIGI VADER5PRO: " + status); }
            catch { /* diagnostic output is not essential to the motors */ }
            try { StatusChanged?.Invoke(); }
            catch { /* consumers cannot break the writer thread */ }
        }

        // Injected in tests; called only on the background thread in production.
        internal static Func<IReadOnlyList<VendorHidCollection>> EnumerateControls =
            () => VendorHidRuntime.Enumerate();
        internal static Func<string, byte[], bool> WriteReport = RawHidOutput.Write;
        internal static Func<long> Clock = () => Environment.TickCount64;

        internal static bool IsDevice(ushort vid, ushort pid)
            => vid == VendorId && pid == ProductId;

        /// <summary>
        /// The native 0x12 backend has physical LT/RT motors even if SDL
        /// reports only its traditional two-motor gamepad interface.
        /// Preserve SDL's reported capabilities for every other device.
        /// </summary>
        internal static bool OffersImpulseTriggerOutput(
            ushort vid, ushort pid, bool sdlHasTriggers, bool nativeEnabled)
            => sdlHasTriggers || (nativeEnabled && IsDevice(vid, pid));

        internal static bool Enabled => Volatile.Read(ref _enabled) != 0;

        /// <summary>10..60 ms, default 30 ms. Sub-30-ms operation is
        /// experimental and must be validated on the connected firmware.</summary>
        internal static int OutputIntervalMs => Volatile.Read(ref _outputIntervalMs);

        internal static void SetOutputIntervalMs(int milliseconds)
        {
            Volatile.Write(ref _outputIntervalMs, Math.Clamp(milliseconds, 10, 60));
            Wake.Set();
        }

        /// <summary>Enabled only by an explicit settings toggle; default off.</summary>
        internal static void SetEnabled(bool enabled)
        {
            bool changed;
            lock (StateGate)
            {
                changed = (Volatile.Read(ref _enabled) != 0) != enabled;
                Volatile.Write(ref _enabled, enabled ? 1 : 0);
                if (!enabled)
                {
                    _desired = default;
                    _owner = Guid.Empty;
                    _ambiguousOwner = false;
                    _lastInputAt = 0;
                    _testUntilAt = 0;
                    // Do not discard the output handle here: the worker owes
                    // the device a ZERO after the last nonzero motor command.
                }
                else if (_worker == null)
                {
                    _worker = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Name = "PadForge.FlydigiVader5ProRumble",
                        Priority = ThreadPriority.Normal,
                    };
                    _worker.Start();
                }
            }
            Wake.Set();
            SetStatus(enabled ? "Searching for 37D7:2401 vendor HID interface" : "Disabled");
            if (changed)
            {
                // Never dispatch a WPF event while holding StateGate.
                try { EnabledChanged?.Invoke(); }
                catch { /* a page listener must not break HID output */ }
            }
        }

        /// <summary>
        /// Poll-thread safe, O(1), no device discovery or I/O. Called every
        /// tick even for unchanged levels, which keeps sustained feedback
        /// alive without misfiring the stale-input safety timeout.
        /// </summary>
        internal static bool Submit(UserDevice device,
            ushort gripLeft, ushort gripRight, ushort triggerLeft, ushort triggerRight)
        {
            if (!Enabled || device == null || device.Device == null
                || !IsDevice(device.VendorId, device.ProdId)) return false;
            var next = new Motors(ToByte(gripLeft), ToByte(gripRight),
                ToByte(triggerLeft), ToByte(triggerRight));
            bool changed;
            lock (StateGate)
            {
                if (!Enabled) return false;
                long now = Clock();
                if (_owner != Guid.Empty && _owner != device.InstanceGuid
                    && now - _lastInputAt <= StaleInputMs)
                {
                    // Two SDL entries for the same VID/PID cannot safely be
                    // associated with the one vendor interface.
                    _ambiguousOwner = true;
                    _desired = default;
                    _lastInputAt = now;
                    Wake.Set();
                    return true;
                }
                if (_owner != device.InstanceGuid && !_ambiguousOwner)
                    _owner = device.InstanceGuid;
                changed = !_desired.Equals(next);
                if (!_ambiguousOwner) _desired = next;
                _lastInputAt = now;
            }
            if (changed) Wake.Set();
            return true; // claimed by sole vendor writer; never fall into SDL
        }

        /// <summary>Send zero on unassignment; never re-route to another pad.</summary>
        internal static void StopDevice(UserDevice device)
        {
            if (device == null || !IsDevice(device.VendorId, device.ProdId)) return;
            lock (StateGate)
            {
                if (_owner != device.InstanceGuid) return;
                _desired = default;
                _lastInputAt = Clock();
                _owner = Guid.Empty;
                _ambiguousOwner = false;
                _testUntilAt = 0;
            }
            Wake.Set();
        }

        /// <summary>Forget paths/owners after a confirmed PnP topology change.</summary>
        internal static void InvalidateDevices()
        {
            // PnP callbacks can run on the 1 kHz input thread. Never wait
            // for an in-flight WriteFile here; retire its handle on the
            // worker under IoGate, before any subsequent re-enumeration.
            lock (StateGate)
            {
                _pendingResetPath = _controlPath ?? _pendingResetPath;
                _controlPath = null;
                _nextScanAt = 0;
                _desired = default;
                _owner = Guid.Empty;
                _ambiguousOwner = false;
                _testUntilAt = 0;
                _everSent = false;
                _lastWriteAt = 0;
            }
            Wake.Set();
        }

        /// <summary>Best-effort synchronous zero on crash or process exit.</summary>
        internal static void StopAllNow()
        {
            lock (IoGate)
            {
                string path;
                lock (StateGate)
                {
                    _desired = default;
                    _owner = Guid.Empty;
                    _ambiguousOwner = false;
                    _testUntilAt = 0;
                    path = _controlPath;
                }
                if (path != null)
                {
                    try { WriteReport(path, BuildReport(default)); }
                    catch { /* machine or device may already be shutting down */ }
                }
                lock (StateGate)
                {
                    _sent = default;
                    _everSent = path != null;
                    _lastWriteAt = Clock();
                }
            }
            Wake.Set();
        }

        /// <summary>Windows HID buffer: ID 06 + 32-byte 5A A5 frame.</summary>
        internal static byte[] BuildReport(Motors motors)
        {
            var report = new byte[33];
            report[0] = 0x06;
            report[1] = 0x5A;
            report[2] = 0xA5;
            report[3] = 0x12;
            report[4] = 0x06; // 2 command/length bytes + 4 motor bytes
            report[5] = motors.GripLeft;
            report[6] = motors.GripRight;
            report[7] = motors.TriggerLeft;
            report[8] = motors.TriggerRight;
            // OpenVader Frame.request checksum: sum of command, length,
            // and payload, modulo 256. Unused bytes remain zero.
            report[9] = unchecked((byte)(report[3] + report[4] + report[5]
                + report[6] + report[7] + report[8]));
            return report;
        }

        internal static byte ToByte(ushort level)
            => (byte)(((uint)level * 255u + 32767u) / 65535u);

        /// <summary>Four 400-ms bench pulses. These bypass the current
        /// game's feedback only while the pulse is active. Uses the same
        /// single-device HID discovery and safety checks as game rumble.</summary>
        internal static Motors TestPattern(int channel, byte strength = 160)
            => channel switch
            {
                0 => new Motors(strength, 0, 0, 0),
                1 => new Motors(0, strength, 0, 0),
                2 => new Motors(0, 0, strength, 0),
                3 => new Motors(0, 0, 0, strength),
                _ => default,
            };

        internal static bool PulseTestMotor(int channel)
        {
            if (!Enabled || channel < 0 || channel > 3) return false;
            lock (StateGate)
            {
                if (!Enabled) return false;
                _testMotors = TestPattern(channel);
                _testUntilAt = Clock() + 400;
            }
            Wake.Set();
            return true;
        }

        internal static bool IsFresh(long now, long lastInput)
            => lastInput > 0 && now >= lastInput && now - lastInput <= StaleInputMs;

        internal static bool MaySend(long now, long lastWrite, bool hasSent,
            int intervalMs = MinimumWriteIntervalMs)
            => !hasSent || now < lastWrite
                || now - lastWrite >= Math.Clamp(intervalMs, 10, 60);

        /// <summary>One zero transition on any physical motor must not
        /// queue behind the full 30 ms rumble cadence. This also detects
        /// just one trigger stopping while the other motors keep going.</summary>
        internal static bool HasStoppingMotor(Motors sent, Motors desired)
            => (sent.GripLeft != 0 && desired.GripLeft == 0)
                || (sent.GripRight != 0 && desired.GripRight == 0)
                || (sent.TriggerLeft != 0 && desired.TriggerLeft == 0)
                || (sent.TriggerRight != 0 && desired.TriggerRight == 0);

        /// <summary>Relative delay until the absolute report deadline.
        /// -1: no write needed for two matching zero reports. Other
        /// matching values refresh only at the 500 ms keepalive. An
        /// urgent zero edge uses a bounded 3 ms gap; all other changes
        /// obey the configured 10..60 ms cadence.</summary>
        internal static int DelayUntilReport(Motors lastSent, Motors desired,
            long now, long lastWrite, bool hasSent, int intervalMs)
        {
            if (!hasSent || now < lastWrite) return 0;
            long elapsed = now - lastWrite;
            if (lastSent.Equals(desired))
                return desired.Silent ? -1 : (int)Math.Max(0L, KeepAliveMs - elapsed);
            int gap = HasStoppingMotor(lastSent, desired)
                ? StopEdgeMinIntervalMs : Math.Clamp(intervalMs, 10, 60);
            return (int)Math.Max(0L, gap - elapsed);
        }

        /// <summary>Strict VID/PID + vendor usage; never guess at a device.</summary>
        internal static string SelectUniqueControlPath(IReadOnlyList<VendorHidCollection> devices)
        {
            if (devices == null) return null;
            string chosen = null;
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                if (d == null || !IsDevice(d.VendorId, d.ProductId)
                    || d.UsagePage != ControlUsagePage || d.Usage != ControlUsage
                    || string.IsNullOrWhiteSpace(d.Path)) continue;
                if (chosen != null)
                {
                    if (!string.Equals(chosen, d.Path, StringComparison.OrdinalIgnoreCase))
                        return null;
                    continue; // same interface with different case: keep first canonical path
                }
                chosen = d.Path;
            }
            return chosen;
        }

        private static void WorkerLoop()
        {
            // Drive by absolute due times, not repeated full-period sleeps.
            // Fast input events can wake the worker before a report is due:
            // the old code then slept another entire interval, adding
            // up to ~30 ms on top of the original 30 ms report budget.
            int waitMs = 0;
            while (true)
            {
                Wake.WaitOne(waitMs);
                waitMs = MaxIdleWaitMs;
                // Always close a stale handle before probing a replacement.
                // IoGate prevents overlap with the previous HID write.
                lock (IoGate)
                {
                    string stalePath;
                    lock (StateGate)
                    {
                        stalePath = _pendingResetPath;
                        _pendingResetPath = null;
                    }
                    if (stalePath != null) RawHidOutput.ResetDevice(stalePath);
                }
                long now = Clock();
                bool enabled = Enabled;
                string path;
                bool needsScan;
                lock (StateGate)
                {
                    path = _controlPath;
                    needsScan = enabled && path == null && now >= _nextScanAt;
                    if (needsScan) _nextScanAt = now + RetryIntervalMs;
                }

                if (needsScan)
                {
                    string discovered = null;
                    try { discovered = SelectUniqueControlPath(EnumerateControls()); }
                    catch { /* no PnP access; re-probe after cooldown */ }
                    lock (StateGate)
                    {
                        if (Enabled && _controlPath == null && discovered != null)
                        {
                            _controlPath = discovered;
                            _everSent = false;
                            _lastWriteAt = 0;
                        }
                        path = _controlPath;
                    }
                    SetStatus(path != null
                        ? "Vader 5 Pro HID interface ready"
                        : "No unique Vader 5 Pro control interface (FFA0:0001)");
                }
                if (path == null) continue;

                lock (IoGate)
                {
                    Motors next;
                    lock (StateGate)
                    {
                        // A disable, stop or disconnect can race discovery.
                        if (_controlPath != path) continue;
                        long sampleAt = Clock();
                        bool testActive = Enabled && !_ambiguousOwner && sampleAt < _testUntilAt;
                        bool fresh = Enabled && !_ambiguousOwner && IsFresh(sampleAt, _lastInputAt);
                        next = testActive ? _testMotors : fresh ? _desired : default;
                        // A static motor command must stop when either a
                        // 400 ms test pulse expires or the input pump stalls.
                        // Otherwise a 500 ms keepalive could delay the ZERO.
                        if (testActive)
                        {
                            long untilTestEnd = _testUntilAt - sampleAt;
                            waitMs = Math.Min(waitMs,
                                (int)Math.Clamp(untilTestEnd, 1L, MaxIdleWaitMs));
                        }
                        else if (fresh && !next.Silent)
                        {
                            long untilStale = _lastInputAt + StaleInputMs + 1 - sampleAt;
                            waitMs = Math.Min(waitMs,
                                (int)Math.Clamp(untilStale, 1L, MaxIdleWaitMs));
                        }

                        int dueIn = DelayUntilReport(
                            _sent, next, sampleAt, _lastWriteAt, _everSent, OutputIntervalMs);
                        if (dueIn < 0) continue;
                        if (dueIn > 0)
                        {
                            // Retry at the REMAINING deadline after an
                            // early wake (rather than waiting 30 ms anew).
                            waitMs = Math.Min(waitMs, dueIn);
                            continue;
                        }
                    }
                    bool ok;
                    try { ok = WriteReport(path, BuildReport(next)); }
                    catch { ok = false; }
                    lock (StateGate)
                    {
                        if (ok)
                        {
                            _sent = next;
                            _everSent = true;
                            _lastWriteAt = Clock();
                        }
                        else
                        {
                            // A vanished receiver never gets a stale handle.
                            _controlPath = null;
                            _everSent = false;
                            _nextScanAt = Clock() + RetryIntervalMs;
                        }
                    }
                    if (!ok)
                    {
                        RawHidOutput.ResetDevice(path);
                        SetStatus("HID write failed; retrying after 1000 ms");
                    }
                    else if (Enabled)
                    {
                        SetStatus("Four-motor HID output available");
                    }
                }
            }
        }
    }
}
