using System.Text;
using System.Threading;
using PadForge.Engine.Common.Logitech;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// The picture on a phone through Logitech Arx Control (issue #519).
    /// LGS 8.52 or later hosts the applet and sends its files to the Arx
    /// Control app over Wi-Fi.
    ///
    /// <para>The applet is one page holding one picture. The page goes out
    /// with <c>LogiArxAddContentAs</c> and <c>LogiArxSetIndex</c> right after
    /// the init, as the SDK's ARX_Test sample and LogitechStuff's CSRadar
    /// send theirs, and again whenever a phone arrives, as ArxWidgetManager's
    /// widgets send theirs. Each new picture goes out with
    /// <c>LogiArxAddImageFromBitmap</c> under one of two names in turn, and
    /// <c>LogiArxSetTagPropertyById</c> points the page's image at it, so the
    /// app never reads a picture that is still being replaced.</para>
    ///
    /// <para>A call that fails with "connection with LGS broken" or "SDK
    /// not initialized" means LGS takes no further call, so the session is
    /// shut down and the init tried again on its timer, the SDK guide's
    /// procedure (pages 8 and 15). The callback does not report LGS exiting,
    /// and a still picture makes no call after its first send, so while the
    /// picture stands one call every <see cref="LivenessMs"/> points the
    /// page's image at the picture it already shows, which changes nothing
    /// and returns true while LGS answers (page 13).</para>
    ///
    /// <para>The callback runs on LGS's thread and only sets flags, the
    /// G-key callback's rule. Every library call is made on the host's
    /// worker.</para>
    /// </summary>
    internal sealed class ArxScreen : System.IDisposable
    {
        private const int LoadRetryMs = 30000;
        internal const int InitRetryMs = 10000;
        private const int IdleShutdownMs = 2000;
        /// <summary>A picture at most this often: LGS compresses each one to
        /// PNG and sends it over Wi-Fi.</summary>
        internal const int MinImageMs = 250;
        /// <summary>How often a still picture's session is asked whether LGS
        /// still answers.</summary>
        internal const int LivenessMs = 5000;
        private const string Identifier = "org.padforge.keyboardscreen";
        private const string ImageTag = "screen";

        private static readonly object s_loadLock = new();
        private static LogitechArxLibrary s_library;
        private static long s_nextLoad;
        private static string s_reason = "missing";

        private static readonly byte[] IndexPage = Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, maximum-scale=1, user-scalable=no\">"
            + "<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}"
            + "img{position:absolute;left:0;top:0;width:100%;height:100%;object-fit:contain}</style></head>"
            + "<body><img id=\"" + ImageTag + "\" src=\"screen0.png\" alt=\"\"></body></html>");

        private readonly System.Func<long, ILogitechArx> _library;
        private bool _active;
        private long _nextInit, _idleSince = -1, _nextImage, _nextLive;
        private long _sent = -1;
        private int _name;
        private int _republish;
        private volatile bool _phone;

        public ArxScreen() : this(Library) { }

        /// <summary>Test seam: the library, null while it does not
        /// load.</summary>
        internal ArxScreen(System.Func<long, ILogitechArx> library) => _library = library;

        public bool Active => _active;

        /// <summary>A phone has arrived since the applet started, and none
        /// has left since.</summary>
        public bool OnPhone => _active && _phone;

        public static string LoadReason
        {
            get { lock (s_loadLock) return s_library != null ? string.Empty : s_reason; }
        }

        private static LogitechArxLibrary Library(long now)
        {
            lock (s_loadLock)
            {
                if (s_library != null || now < s_nextLoad) return s_library;
                s_nextLoad = now + LoadRetryMs;
                s_library = LogitechArxLibrary.TryLoad(out s_reason);
                return s_library;
            }
        }

        public void Step(byte[] frame, long version, long now)
        {
            var lib = _library(now);
            if (lib == null) return;

            if (frame == null)
            {
                if (!_active) return;
                if (_idleSince < 0) _idleSince = now;
                if (now - _idleSince >= IdleShutdownMs) Shutdown(lib);
                return;
            }
            _idleSince = -1;

            if (!_active)
            {
                if (now < _nextInit) return;
                if (!lib.Init(Identifier, "PadForge", OnEvent))
                {
                    _nextInit = now + InitRetryMs;
                    return;
                }
                _active = true;
                _phone = false;
                _sent = -1;
                Interlocked.Exchange(ref _republish, 1);
            }

            if (Interlocked.Exchange(ref _republish, 0) == 1)
            {
                // The image first, so the page's first load finds it.
                bool sent = SendImage(lib, frame);
                if (Lost(lib, sent, now)) return;
                if (sent) _sent = version;
                if (Lost(lib, lib.AddContent(IndexPage, "index.html", "text/html"), now)
                    || Lost(lib, lib.SetIndex("index.html"), now)
                    || Lost(lib, lib.SetTagProperty(ImageTag, "src", CurrentName), now)) return;
                _nextImage = now + MinImageMs;
                _nextLive = now + LivenessMs;
                return;
            }

            if (_sent == version)
            {
                if (now < _nextLive) return;
                _nextLive = now + LivenessMs;
                Lost(lib, lib.SetTagProperty(ImageTag, "src", CurrentName), now);
                return;
            }
            if (now < _nextImage) return;
            _nextImage = now + MinImageMs;
            _nextLive = now + LivenessMs;
            _name ^= 1;
            bool drawn = SendImage(lib, frame);
            if (Lost(lib, drawn, now) || !drawn) return;
            if (Lost(lib, lib.SetTagProperty(ImageTag, "src", CurrentName), now)) return;
            _sent = version;
        }

        private string CurrentName => _name == 0 ? "screen0.png" : "screen1.png";

        private bool SendImage(ILogitechArx lib, byte[] frame)
            => lib.AddImage(frame, KeyboardScreenFormats.ArxWidth, KeyboardScreenFormats.ArxHeight, CurrentName);

        /// <summary>Whether a call's failure says the session is gone, in
        /// which case it is shut down and the init tried again after
        /// <see cref="InitRetryMs"/>. Any other failure leaves the session
        /// up, and the picture goes out again with the next one.</summary>
        private bool Lost(ILogitechArx lib, bool ok, long now)
        {
            if (ok) return false;
            int error = lib.LastError;
            if (error != LogitechArxLibrary.ErrorConnectionBroken && error != LogitechArxLibrary.ErrorNotInitialized) return false;
            Shutdown(lib);
            _nextInit = now + InitRetryMs;
            return true;
        }

        /// <summary>LGS's thread: flags only.</summary>
        private void OnEvent(uint type, uint value)
        {
            if (type == LogitechArxLibrary.EventDeviceArrival)
            {
                _phone = true;
                Interlocked.Exchange(ref _republish, 1);
            }
            else if (type == LogitechArxLibrary.EventDeviceRemoval)
            {
                _phone = false;
            }
        }

        private void Shutdown(ILogitechArx lib)
        {
            lib.Shutdown();
            _active = false;
            _phone = false;
            _idleSince = -1;
        }

        public void Dispose()
        {
            if (!_active) return;
            // A time before any load answers the library already loaded and
            // never loads one.
            var lib = _library(long.MinValue);
            if (lib != null) Shutdown(lib);
        }
    }
}
