using System;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.ForceDimension;
using PadForge.Engine.Data;

namespace PadForge.Common.Input.Falcon
{
    /// <summary>What the poll thread hands a Falcon's haptic thread at each
    /// pass (issue #521): the first assigned slot's settings and the game's
    /// force, or nothing for a Falcon no slot is assigned.</summary>
    internal sealed record FalconHandoff(FalconSettings Settings, FalconGameForce Game, bool Assigned)
    {
        public static readonly FalconHandoff Unassigned =
            new(FalconSettings.Unassigned, FalconGameForce.Silent, false);
    }

    /// <summary>A calibration sweep's result: the reach on each axis, and
    /// which axes the sweep measured inside the accepted span.</summary>
    internal readonly record struct FalconCalibration(FalconReach Reach, bool MeasuredX, bool MeasuredY, bool MeasuredZ);

    /// <summary>What a Falcon last read and wrote, for the row and the
    /// Devices page.</summary>
    internal sealed record FalconReading(double StickX, double StickY, double InOut, uint Buttons)
    {
        public static readonly FalconReading Rest = new(0, 0, 0, 0);
    }

    /// <summary>
    /// A Novint Falcon opened through Force Dimension's library (issue
    /// #521), as one device row: the handle's Left/Right, Up/Down and In/Out
    /// and the grip's four buttons. <see cref="LogitechGKeysDevice"/>'s
    /// shape for the row and <see cref="HeadTrackerDevice"/>'s for the
    /// axes, with a haptic thread of its own.
    ///
    /// <para>The haptic thread runs unpaced at the highest priority, the
    /// library's calls holding it near 1 kHz as they hold FalconJoyForce's
    /// loop (FalconJoyForce.cpp:2039-2043), one thread per device as Force
    /// Dimension's multithread example runs them. Its time step comes from
    /// a <see cref="Stopwatch"/>, since a 1 ms tick corrupts the filters
    /// (FalconJoyForce.cpp:1459-1461). Each pass reads the handle and the
    /// buttons, runs <see cref="FalconController"/> and sets the force, and
    /// publishes the reading the row reports.</para>
    /// </summary>
    internal sealed class FalconDevice : ISdlInputDevice
    {
        public const int AxisCount = 3;
        public const int ButtonCount = 4;
        public const int AxisLeftRight = 0, AxisUpDown = 1, AxisInOut = 2;

        /// <summary>The Falcon's USB ID (libnifalcon <c>FalconComm.h</c>,
        /// Force Dimension's <c>fdhusb.inf</c>).</summary>
        public const ushort FalconVendorId = 0x0403, FalconProductId = 0xCB48;

        /// <summary><c>DHD_DEVICE_FALCON</c> (<c>dhdc.h</c>).</summary>
        public const int SystemTypeFalcon = 60;

        /// <summary>Standard gravity, the SDK's default for gravity
        /// compensation.</summary>
        public const double StandardGravity = 9.81;

        /// <summary>A calibration sweep's length and its accepted span,
        /// FalconJoyForce's F11 sweep (FalconJoyForce.cpp:2014-2031,
        /// <c>CALIB_FILL</c>, <c>CALIB_MIN</c>, <c>CALIB_MAX</c> :122-124).</summary>
        public const double CalibrationMs = 4000, CalibrationFill = 0.95, CalibrationMin = 0.025, CalibrationMax = 0.120;

        /// <summary>Failed reads in a row, or failed force writes in a row,
        /// before the Falcon counts as gone.</summary>
        public const int FailuresToDetach = 3;

        private readonly IFalconLink _link;
        private readonly int _id;
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _attached;
        private volatile bool _disposed;
        private volatile FalconHandoff _handoff = FalconHandoff.Unassigned;
        private volatile ReachBox _reach = new(FalconReach.Default);
        private volatile FalconReading _reading = FalconReading.Rest;
        // Read with Volatile and swapped with Interlocked: a volatile field
        // passed by reference is no longer treated as volatile.
        private Action<FalconCalibration> _calibrationDone;
        private PooledInputStatePair _statePool;

        private sealed record ReachBox(FalconReach Value);

        /// <summary>Opens nothing: the sweep opened the device by its index
        /// and checked its type. This starts its haptic thread.</summary>
        public FalconDevice(IFalconLink link, int id, int index, ushort? serial)
        {
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _id = id;
            Index = index;
            Serial = serial;
            string key = serial.HasValue
                ? "pffalcon-serial-" + serial.Value.ToString(CultureInfo.InvariantCulture)
                : "pffalcon-index-" + index.ToString(CultureInfo.InvariantCulture);
            InstanceGuid = Md5Guid(key);
            ProductGuid = Md5Guid("pffalcon-product");
            DevicePath = "falcon://" + (serial.HasValue
                ? serial.Value.ToString(CultureInfo.InvariantCulture)
                : "index/" + index.ToString(CultureInfo.InvariantCulture));
            SerialNumber = serial.HasValue ? serial.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            OpenedAtTicks = Environment.TickCount64;
            _attached = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge.Falcon." + SerialNumber, Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        /// <summary>The enumeration index the sweep opened it at.</summary>
        public int Index { get; }
        public int Id => _id;
        public ushort? Serial { get; }

        /// <summary>When it opened, for the homing line.</summary>
        public long OpenedAtTicks { get; }

        /// <summary>The rate Force Dimension's library last reported, in
        /// kHz, refreshed once a second.</summary>
        public double RateKhz { get; private set; }

        public bool IsCalibrating => Volatile.Read(ref _calibrationDone) != null;

        public FalconReach Reach
        {
            get => _reach.Value;
            set => _reach = new ReachBox(value);
        }

        /// <summary>The poll thread's hand-off. Read once per pass.</summary>
        public FalconHandoff Handoff
        {
            get => _handoff;
            set => _handoff = value ?? FalconHandoff.Unassigned;
        }

        public FalconReading Reading => _reading;

        // The poll thread's caches, so a pass whose settings and force match
        // the last one hands over nothing new. Poll thread only.
        private readonly string[] _settingsKey = new string[11];
        private FalconSettings _settings = FalconSettings.Unassigned;
        private bool _settingsValid;
        private FalconGameForce _game = FalconGameForce.Silent;
        private bool _gameValid;
        private ushort _gameLeft, _gameRight;
        private double _gameOverall;
        private bool _gameDirectional, _gameCondition;
        private uint _gameType, _gamePeriod;
        private short _gameMagnitude;
        private ushort _gameDirection;
        private byte _gameGain;
        private ConditionAxisData[] _gameAxes = Array.Empty<ConditionAxisData>();

        /// <summary>Step 2's hand-off for a Falcon its slots drive: the first
        /// slot's settings and the combined force, with the motors after
        /// Trigger Rumble Fold. Allocates only when either changed.</summary>
        internal void Publish(PadSetting ps, Vibration v, ushort left, ushort right)
        {
            var settings = SettingsFor(ps);
            var game = GameFor(v, left, right, settings.Overall);
            var current = _handoff;
            if (current.Assigned && ReferenceEquals(current.Settings, settings) && ReferenceEquals(current.Game, game))
                return;
            _handoff = new FalconHandoff(settings, game, true);
        }

        /// <summary>An engine silence edge: the feel stays, the game's force
        /// goes.</summary>
        internal void SilenceGame()
        {
            var current = _handoff;
            _gameValid = false;
            if (!ReferenceEquals(current.Game, FalconGameForce.Silent))
                _handoff = current with { Game = FalconGameForce.Silent };
        }

        private FalconSettings SettingsFor(PadSetting ps)
        {
            if (ps == null) return FalconSettings.Unassigned;
            bool same = _settingsValid;
            void Check(int i, string value)
            {
                if (!string.Equals(_settingsKey[i], value, StringComparison.Ordinal)) { same = false; _settingsKey[i] = value; }
            }
            Check(0, ps.FalconFeel);
            Check(1, ps.FalconSpring);
            Check(2, ps.FalconKickButton);
            Check(3, ps.FalconKick);
            Check(4, ps.FalconShake);
            Check(5, ps.FalconBraceButton);
            Check(6, ps.FalconPreloadButton);
            Check(7, ps.FalconGravity);
            Check(8, ps.FalconForceCap);
            Check(9, ps.ForceOverall);
            if (!same)
            {
                _settings = FalconSettings.From(ps);
                _settingsValid = true;
            }
            return _settings;
        }

        private FalconGameForce GameFor(Vibration v, ushort left, ushort right, double overall)
        {
            if (v == null) return FalconGameForce.Silent;
            int count = v.HasConditionData && v.ConditionAxes != null
                ? Math.Min(v.ConditionAxisCount, v.ConditionAxes.Length) : 0;
            bool same = _gameValid && left == _gameLeft && right == _gameRight && overall == _gameOverall
                && v.HasDirectionalData == _gameDirectional && v.HasConditionData == _gameCondition
                && v.EffectType == _gameType && v.SignedMagnitude == _gameMagnitude && v.Direction == _gameDirection
                && v.Period == _gamePeriod && v.DeviceGain == _gameGain && count == _gameAxes.Length;
            for (int i = 0; same && i < count; i++)
                same = SameAxis(v.ConditionAxes[i], _gameAxes[i]);
            if (same) return _game;

            _gameLeft = left;
            _gameRight = right;
            _gameOverall = overall;
            _gameDirectional = v.HasDirectionalData;
            _gameCondition = v.HasConditionData;
            _gameType = v.EffectType;
            _gameMagnitude = v.SignedMagnitude;
            _gameDirection = v.Direction;
            _gamePeriod = v.Period;
            _gameGain = v.DeviceGain;
            _gameAxes = count == 0 ? Array.Empty<ConditionAxisData>() : v.ConditionAxes.AsSpan(0, count).ToArray();
            _game = FalconGameForce.From(v, left, right, overall);
            _gameValid = true;
            return _game;
        }

        // Field by field: the struct's own Equals boxes its argument, which
        // would allocate at the poll rate.
        private static bool SameAxis(in ConditionAxisData a, in ConditionAxisData b)
            => a.PositiveCoefficient == b.PositiveCoefficient && a.NegativeCoefficient == b.NegativeCoefficient
               && a.Offset == b.Offset && a.DeadBand == b.DeadBand
               && a.PositiveSaturation == b.PositiveSaturation && a.NegativeSaturation == b.NegativeSaturation;

        /// <summary>Starts a four-second sweep: move the handle to every
        /// extreme. The forces rest while it runs. <paramref name="done"/>
        /// runs on the haptic thread, or at once on the caller's when that
        /// thread has already ended, with no axis measured. False while one
        /// already runs, and after Dispose.</summary>
        public bool BeginCalibration(Action<FalconCalibration> done)
        {
            if (done == null || _disposed) return false;
            if (Interlocked.CompareExchange(ref _calibrationDone, done, null) != null) return false;
            // The haptic thread clears _attached before it takes back the
            // stored sweep on its way out. A sweep stored after that would
            // never be answered, so it is taken back here. The exchange
            // lets exactly one of the two answer it.
            if (!_attached && Interlocked.CompareExchange(ref _calibrationDone, null, done) == done)
            {
                try { done(Measure(_reach.Value, 0, 0, 0)); }
                catch (Exception) { }
            }
            return true;
        }

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name => "Novint Falcon";
        public int NumAxes => AxisCount;
        public int NumButtons => ButtonCount;
        public int RawButtonCount => ButtonCount;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => null;
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => FalconVendorId;
        public ushort ProductId => FalconProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber { get; }
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.NovintFalcon;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        public bool Open() => IsAttached;

        /// <summary>The three handle axes and the four grip buttons, named in
        /// English whatever the language: the row keeps its objects in the
        /// settings file and is not opened again when the language changes.
        /// MappingDisplayResolver.LocalizeObjectName translates the names
        /// where they are shown.</summary>
        public DeviceObjectItem[] GetDeviceObjects() => DeviceObjects();

        internal static DeviceObjectItem[] DeviceObjects()
        {
            var items = new DeviceObjectItem[AxisCount + ButtonCount];
            items[0] = Axis(AxisNameLeftRight, ObjectGuid.XAxis, AxisLeftRight);
            items[1] = Axis(AxisNameUpDown, ObjectGuid.YAxis, AxisUpDown);
            items[2] = Axis(AxisNameInOut, ObjectGuid.ZAxis, AxisInOut);
            for (int b = 0; b < ButtonCount; b++)
                items[AxisCount + b] = new DeviceObjectItem
                {
                    Name = ButtonName(b),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = b,
                };
            return items;
        }

        private static DeviceObjectItem Axis(string name, Guid guid, int index) => new()
        {
            Name = name,
            ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
            ObjectTypeGuid = guid,
            InputIndex = index,
            Offset = index * 4,
        };

        internal const string AxisNameLeftRight = "Left/Right";
        internal const string AxisNameUpDown = "Up/Down";
        internal const string AxisNameInOut = "In/Out";

        /// <summary>A grip button's English name, numbered from 1 in Force
        /// Dimension's order. "Button N" is the name every numbered button
        /// is translated from.</summary>
        internal static string ButtonName(int button)
            => string.Format(CultureInfo.InvariantCulture, "Button {0}", button + 1);

        /// <summary>The reading as the row reports it: SDL's signed range
        /// moved to 0 to 65535, with up the low end as on a gamepad's Y axis,
        /// so the default mapping puts the handle on the right stick the way
        /// a gamepad's right stick maps.</summary>
        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            var r = _reading;
            var s = _statePool.Next();
            s.Axis[AxisLeftRight] = ToAxis(r.StickX);
            s.Axis[AxisUpDown] = ToAxis(-r.StickY);
            s.Axis[AxisInOut] = ToAxis(r.InOut);
            for (int b = 0; b < ButtonCount; b++)
                s.Buttons[b] = (r.Buttons & (1u << b)) != 0;
            return s;
        }

        internal static int ToAxis(double v)
        {
            int signed = (int)Math.Round(Math.Clamp(v, -1.0, 1.0) * 32767.0);
            return signed - short.MinValue;
        }

        // ─── The haptic thread ───

        private void Run()
        {
            int seed = Serial ?? Index;
            var controller = new FalconController(seed);
            double gravity = -1, cap = -1;
            // Counted apart, so a good read between two failed writes never
            // resets the writes' count.
            int readFailures = 0, writeFailures = 0;
            var clock = Stopwatch.StartNew();
            double last = clock.Elapsed.TotalMilliseconds;
            double nextRate = last + 1000;
            double calibrationEnd = 0;
            double minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
            bool calibrating = false;
            try
            {
                // Force Dimension's examples stop when dhdEnableForce fails
                // (gravity.cpp, multithread.cpp). A Falcon whose gravity
                // compensation or forces do not turn on closes here, and the
                // next sweep opens it again.
                if (_link.SetGravityCompensation(_id, true) < 0)
                {
                    LogClose("could not turn on gravity compensation");
                    return;
                }
                if (_link.EnableForce(_id, true) < 0)
                {
                    LogClose("could not turn on its forces");
                    return;
                }
                while (!_stop)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    double dt = (now - last) / 1000.0;
                    last = now;
                    if (dt <= 0.0) dt = 0.0005;
                    if (dt > 0.05) dt = 0.01;

                    if (_link.GetPosition(_id, out double x, out double y, out double z) < 0)
                    {
                        if (++readFailures >= FailuresToDetach)
                        {
                            LogClose("failed " + FailuresToDetach + " reads in a row");
                            break;
                        }
                        Thread.Sleep(1);
                        continue;
                    }
                    readFailures = 0;
                    uint buttons = _link.ButtonMask(_id) & ((1u << ButtonCount) - 1);
                    var handoff = _handoff;
                    var set = handoff.Settings ?? FalconSettings.Unassigned;

                    double wantGravity = StandardGravity * (handoff.Assigned ? set.Gravity : 1.0);
                    if (wantGravity != gravity)
                    {
                        _link.SetStandardGravity(_id, wantGravity);
                        gravity = wantGravity;
                    }
                    if (set.ForceCap != cap)
                    {
                        _link.SetMaxForce(_id, set.ForceCap);
                        cap = set.ForceCap;
                    }

                    var reach = _reach.Value;
                    FalconOutput output;
                    if (Volatile.Read(ref _calibrationDone) != null)
                    {
                        if (!calibrating)
                        {
                            calibrating = true;
                            calibrationEnd = now + CalibrationMs;
                            minX = maxX = x;
                            minY = maxY = y;
                            minZ = maxZ = z;
                        }
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                        // The handle rests while the user sweeps it.
                        output = new FalconOutput(0, 0, 0, 0, 0, 0);
                        if (now >= calibrationEnd)
                        {
                            calibrating = false;
                            var result = Measure(reach, maxX - minX, maxY - minY, maxZ - minZ);
                            Reach = result.Reach;
                            var done = Interlocked.Exchange(ref _calibrationDone, null);
                            try { done?.Invoke(result); }
                            catch (Exception) { }
                        }
                    }
                    else
                    {
                        output = controller.Step(new FalconSample(x, y, z, buttons), reach, set,
                            handoff.Game, handoff.Assigned, dt, now);
                    }

                    // dhdSetForce answers -1 for a failed write, and 0 or
                    // DHD_MOTOR_SATURATED for one that went through. Force
                    // Dimension's examples close the device at the first
                    // failure (multithread.cpp, gravity.cpp).
                    if (_link.SetForce(_id, output.Fx, output.Fy, output.Fz) < 0)
                    {
                        if (++writeFailures >= FailuresToDetach)
                        {
                            LogClose("failed " + FailuresToDetach + " force writes in a row");
                            break;
                        }
                    }
                    else writeFailures = 0;
                    _reading = new FalconReading(output.StickX, output.StickY, output.InOut, buttons);

                    if (now >= nextRate)
                    {
                        nextRate = now + 1000;
                        RateKhz = _link.ComFrequency(_id);
                    }
                }
            }
            catch (Exception)
            {
                // A library that throws is a Falcon gone.
            }
            finally
            {
                _attached = false;
                _reading = FalconReading.Rest;
                try { _link.SetForce(_id, 0, 0, 0); } catch (Exception) { }
                try { _link.Close(_id); } catch (Exception) { }
                var done = Interlocked.Exchange(ref _calibrationDone, null);
                try { done?.Invoke(Measure(_reach.Value, 0, 0, 0)); } catch (Exception) { }
            }
        }

        /// <summary>Logs why the haptic thread closes the Falcon, with the
        /// library's own reason. The library keeps its last error per thread,
        /// so this runs on the haptic thread right after the failed
        /// call.</summary>
        private void LogClose(string why)
            => SdlDiagLog.WriteLine($"Falcon: device {_id} {why}, so it closes: {_link.LastErrorText()}");

        /// <summary>A sweep's spans as reaches: 95% of half the span, kept
        /// only inside 25 to 120 mm, so a partial sweep leaves the old reach.</summary>
        internal static FalconCalibration Measure(FalconReach old, double spanX, double spanY, double spanZ)
        {
            static bool Fit(double span, double fallback, out double reach)
            {
                double fit = span * 0.5 * CalibrationFill;
                bool ok = fit >= CalibrationMin && fit <= CalibrationMax;
                reach = ok ? fit : fallback;
                return ok;
            }
            bool okX = Fit(spanX, old.X, out double rx);
            bool okY = Fit(spanY, old.Y, out double ry);
            bool okZ = Fit(spanZ, old.Z, out double rz);
            return new FalconCalibration(new FalconReach(rx, ry, rz), okX, okY, okZ);
        }

        /// <summary>The crash quiesce: stops the haptic thread, which sets zero
        /// force and closes the device, waiting until the
        /// <see cref="Environment.TickCount64"/> value
        /// <paramref name="untilTicks"/>. A thread still inside a call by then
        /// gets the zero force from here, the library being thread-safe, and
        /// closes the device when the call returns.</summary>
        public void Quiesce(long untilTicks)
        {
            _stop = true;
            int wait = (int)Math.Clamp(untilTicks - Environment.TickCount64, 0, int.MaxValue);
            if (Thread.CurrentThread == _thread || _thread.Join(wait)) return;
            try { _link.SetForce(_id, 0, 0, 0); } catch (Exception) { }
        }

        /// <summary>Stops the haptic thread, which sets zero force and closes
        /// the device. Blocks up to two seconds, so the caller runs it off
        /// the poll thread.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            if (Thread.CurrentThread != _thread && !_thread.Join(2000))
            {
                // A thread stuck in a call keeps its device. The library is
                // thread-safe, so the stop is sent from here.
                try { _link.SetForce(_id, 0, 0, 0); } catch (Exception) { }
                try { _link.Close(_id); } catch (Exception) { }
            }
            _attached = false;
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }

        // ─── Calibration as stored on the device's record ───

        /// <summary>A reach as the record keeps it: "x,y,z" in millimeters.</summary>
        internal static string FormatReach(FalconReach reach)
            => string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0}",
                reach.X * 1000.0, reach.Y * 1000.0, reach.Z * 1000.0);

        /// <summary>The record's reach, the default for an empty or
        /// unreadable value and for any axis outside the accepted span.</summary>
        internal static FalconReach ParseReach(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return FalconReach.Default;
            var parts = stored.Split(',');
            if (parts.Length != 3) return FalconReach.Default;
            double Axis(string s)
            {
                if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mm)
                    || !double.IsFinite(mm))
                    return FalconReach.DefaultMeters;
                double m = mm / 1000.0;
                return m >= CalibrationMin && m <= CalibrationMax ? m : FalconReach.DefaultMeters;
            }
            return new FalconReach(Axis(parts[0]), Axis(parts[1]), Axis(parts[2]));
        }
    }
}
