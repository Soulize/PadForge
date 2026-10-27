using System;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One keyboard screen as a device row (issue #519): the keys beside the
    /// screen as buttons, and the row whose panel on the Devices page sets
    /// what the screen shows, the Dreamcast Screen button's place.
    /// <see cref="LogitechGKeysDevice"/>'s shape: a synthetic
    /// <see cref="ISdlInputDevice"/> that binds through the grid every other
    /// device uses. The engines report what they read into
    /// <see cref="KeyboardScreenKeys"/> under the screen's id, which holds a
    /// press for at least <see cref="KeyboardScreenKeys.PulseMs"/>. Arx
    /// Control and the S.T.R.I.K.E. 7 have no keys, so their rows have no
    /// buttons and carry the screen alone.
    /// </summary>
    internal sealed class KeyboardScreenDevice : ISdlInputDevice
    {
        // "KS", the synthetic identity convention the handheld row set, and a
        // product ID per kind: "KM", "KC", "KA" and "K7".
        private const ushort ScreenVendorId = 0x4B53;

        private volatile bool _attached;
        private volatile bool _disposed;

        public KeyboardScreenDevice(KeyboardScreenInfo screen)
        {
            Screen = screen ?? throw new ArgumentNullException(nameof(screen));
            Name = screen.Name;
            DevicePath = "keyboardscreen://" + screen.Id;
            InstanceGuid = screen.RowGuid;
            ProductGuid = KeyboardScreenInfo.RowGuidOf("product|" + screen.Kind);
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        /// <summary>The screen this row is.</summary>
        public KeyboardScreenInfo Screen { get; }

        internal static ushort ProductIdOf(KeyboardScreenKind kind) => kind switch
        {
            KeyboardScreenKind.Monochrome => 0x4B4D,
            KeyboardScreenKind.Color => 0x4B43,
            KeyboardScreenKind.Arx => 0x4B41,
            _ => 0x4B37,
        };

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => KeyboardScreenFormats.KeyCountOf(Screen.Kind);
        public int RawButtonCount => NumButtons;
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
        public ushort VendorId => ScreenVendorId;
        public ushort ProductId => ProductIdOf(Screen.Kind);
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.KeyboardScreenKeys;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>The screen's keys in its kind's fixed order.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[NumButtons];
            for (int i = 0; i < items.Length; i++)
                items[i] = new DeviceObjectItem
                {
                    Name = KeyName(Screen.Kind, i),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = i,
                };
            return items;
        }

        /// <summary>A key's name as the row publishes it, in English whatever
        /// the language: the row keeps its objects in the settings file and
        /// is not opened again when the language changes, so a translated
        /// name would stay in the language the screen was found in.
        /// <see cref="MappingDisplayResolver.LocalizeObjectName"/> translates
        /// it where it is shown, as it does the NFC row's "Any NFC
        /// Tag".</summary>
        internal static string KeyName(KeyboardScreenKind kind, int button)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            if (kind == KeyboardScreenKind.Monochrome)
                return button switch
                {
                    >= KeyboardScreenFormats.MonoKey1 and < KeyboardScreenFormats.MonoAppletKey
                        => string.Format(culture, MonoKeyPrefix + "{0}", button - KeyboardScreenFormats.MonoKey1 + 1),
                    KeyboardScreenFormats.MonoAppletKey => AppletKeyName,
                    _ => string.Format(culture, "Button {0}", button + 1),
                };
            return button switch
            {
                KeyboardScreenFormats.G19Left => "Screen Left",
                KeyboardScreenFormats.G19Right => "Screen Right",
                KeyboardScreenFormats.G19Ok => "Screen OK",
                KeyboardScreenFormats.G19Cancel => "Screen Cancel",
                KeyboardScreenFormats.G19Up => "Screen Up",
                KeyboardScreenFormats.G19Down => "Screen Down",
                KeyboardScreenFormats.G19Menu => "Screen Menu",
                KeyboardScreenFormats.G19Settings => "Screen Settings",
                _ => string.Format(culture, "Button {0}", button + 1),
            };
        }

        /// <summary>The English name of a monochrome screen's numbered soft
        /// key, before its number.</summary>
        internal const string MonoKeyPrefix = "Screen Key ";

        internal const string AppletKeyName = "Applet Key";

        // ─── Lifecycle ───

        public bool Open()
        {
            if (_disposed) return false;
            _attached = true;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
        }

        // ─── State read (poll thread) ───

        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            var s = _statePool.Next();
            KeyboardScreenKeys.Read(Screen.Id, s.Buttons, Environment.TickCount64);
            return s;
        }
    }
}
