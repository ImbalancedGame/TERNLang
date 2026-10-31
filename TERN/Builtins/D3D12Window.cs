using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// The window half of <c>d3d12</c>: Win32 through <c>user32.dll</c>,
    /// <c>gdi32.dll</c> and <c>kernel32.dll</c>, opened at run time so there is
    /// no interop assembly and no build-time dependency on Windows.
    ///
    /// The native backend's <c>native/runtime/tern_window.h</c> does the same
    /// thing with the same numbers: the structures below are laid out by hand and
    /// the offsets are checked against the real <c>windows.h</c> by
    /// <c>tools/d3d12/window_layout_check.py</c>.
    ///
    /// <c>TERN_USER32_LIBRARY</c>, <c>TERN_GDI32_LIBRARY</c> and
    /// <c>TERN_KERNEL32_LIBRARY</c> override the library names, which is how the
    /// mock in <c>tools/d3d12/mock_user32.c</c> answers on Linux.
    /// </summary>
    internal sealed class D3D12Window : IDisposable
    {
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const int SwShow = 5;
        private const uint PmRemove = 0x0001;
        private const uint WmKeyDown = 0x0100;
        private const uint WmClose = 0x0010;
        private const uint WmDestroy = 0x0002;
        private const uint WmQuit = 0x0012;
        private const uint VkEscape = 0x1B;
        private const uint SrcCopy = 0x00CC0020;
        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;
        private const uint CpUtf8 = 65001;
        private static readonly IntPtr IdiArrow = (IntPtr)32512;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort Bits;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMetre;
            public int YPelsPerMetre;
            public uint ColoursUsed;
            public uint ColoursImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapHeader Header;
            public uint Colour0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public IntPtr Proc;
            public int ClsExtra;
            public int WndExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public IntPtr Menu;
            public IntPtr ClassName;
            public IntPtr IconSmall;
        }

        private delegate IntPtr WindowProcFn(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        private delegate ushort RegisterClassFn(ref WindowClass windowClass);
        private delegate IntPtr CreateWindowFn(uint exStyle, IntPtr className, IntPtr windowName,
                                               uint style, int x, int y, int width, int height,
                                               IntPtr parent, IntPtr menu, IntPtr instance,
                                               IntPtr parameter);
        private delegate IntPtr DefWindowProcFn(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        private delegate bool ShowWindowFn(IntPtr hwnd, int command);
        private delegate bool UpdateWindowFn(IntPtr hwnd);
        private delegate bool PeekMessageFn(ref Msg message, IntPtr hwnd, uint min, uint max, uint remove);
        private delegate bool TranslateMessageFn(ref Msg message);
        private delegate IntPtr DispatchMessageFn(ref Msg message);
        private delegate bool DestroyWindowFn(IntPtr hwnd);
        private delegate IntPtr GetDcFn(IntPtr hwnd);
        private delegate int ReleaseDcFn(IntPtr hwnd, IntPtr dc);
        private delegate IntPtr ModuleHandleFn(IntPtr name);
        private delegate IntPtr LoadCursorFn(IntPtr instance, IntPtr name);
        private delegate bool GetClientRectFn(IntPtr hwnd, out Rect rect);
        private delegate bool AdjustWindowRectFn(ref Rect rect, uint style, bool menu);
        private delegate int StretchDibFn(IntPtr dc, int xDest, int yDest, int destWidth, int destHeight,
                                          int xSrc, int ySrc, int srcWidth, int srcHeight,
                                          IntPtr bits, ref BitmapInfo info, uint usage, uint rop);
        private delegate void SleepFn(uint milliseconds);
        private delegate int MultiByteToWideCharFn(uint codePage, uint flags, [MarshalAs(UnmanagedType.LPStr)] string text,
                                                   int count, IntPtr wide, int wideCount);

        private sealed class Api
        {
            public IntPtr User32, Gdi32, Kernel32;
            public RegisterClassFn? RegisterClass;
            public CreateWindowFn? CreateWindow;
            public DefWindowProcFn? DefWindowProc;
            public ShowWindowFn? ShowWindow;
            public UpdateWindowFn? UpdateWindow;
            public PeekMessageFn? PeekMessage;
            public TranslateMessageFn? TranslateMessage;
            public DispatchMessageFn? DispatchMessage;
            public DestroyWindowFn? DestroyWindow;
            public GetDcFn? GetDc;
            public ReleaseDcFn? ReleaseDc;
            public ModuleHandleFn? ModuleHandle;
            public LoadCursorFn? LoadCursor;
            public GetClientRectFn? GetClientRect;
            public AdjustWindowRectFn? AdjustWindowRect;
            public StretchDibFn? StretchDib;
            public SleepFn? Sleep;
            public MultiByteToWideCharFn? Widen;
        }

        private static Api? _api;
        private static bool _attempted;
        private static IntPtr _className;
        private static bool _classRegistered;

        private readonly IntPtr _hwnd;
        private readonly int _width;
        private readonly int _height;
        private byte[] _dib = Array.Empty<byte>();
        private int _dibWidth;
        private int _dibHeight;

        private D3D12Window(IntPtr hwnd, int width, int height)
        {
            _hwnd = hwnd;
            _width = width;
            _height = height;
        }

        public static bool Supported => Load() is not null;

        /// <summary>The Win32 handle, for the swap chain that presents into it.</summary>
        internal IntPtr Handle => _hwnd;

        /// <summary>
        /// Processes the window's messages and answers whether it is still open.
        /// What <c>d3d12.present</c> calls once the frame is on the swap chain.
        /// </summary>
        public bool PumpMessages()
        {
            var api = Load();
            if (api is null || Closed)
            {
                return false;
            }

            Pump(api);
            return !Closed;
        }

        private static T? Export<T>(IntPtr library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out var address))
            {
                return null;
            }

            return (T?)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static Api? Load()
        {
            if (_attempted)
            {
                return _api;
            }

            _attempted = true;

            var user32Name = Environment.GetEnvironmentVariable("TERN_USER32_LIBRARY");
            var gdi32Name = Environment.GetEnvironmentVariable("TERN_GDI32_LIBRARY");
            var kernel32Name = Environment.GetEnvironmentVariable("TERN_KERNEL32_LIBRARY");
            if (!NativeLibrary.TryLoad(string.IsNullOrEmpty(user32Name) ? "user32.dll" : user32Name, out var user32))
            {
                return null;
            }

            if (!NativeLibrary.TryLoad(string.IsNullOrEmpty(gdi32Name) ? "gdi32.dll" : gdi32Name, out var gdi32))
            {
                return null;
            }

            NativeLibrary.TryLoad(string.IsNullOrEmpty(kernel32Name) ? "kernel32.dll" : kernel32Name, out var kernel32);

            var api = new Api
            {
                User32 = user32,
                Gdi32 = gdi32,
                Kernel32 = kernel32,
                RegisterClass = Export<RegisterClassFn>(user32, "RegisterClassExW"),
                CreateWindow = Export<CreateWindowFn>(user32, "CreateWindowExW"),
                DefWindowProc = Export<DefWindowProcFn>(user32, "DefWindowProcW"),
                ShowWindow = Export<ShowWindowFn>(user32, "ShowWindow"),
                UpdateWindow = Export<UpdateWindowFn>(user32, "UpdateWindow"),
                PeekMessage = Export<PeekMessageFn>(user32, "PeekMessageW"),
                TranslateMessage = Export<TranslateMessageFn>(user32, "TranslateMessage"),
                DispatchMessage = Export<DispatchMessageFn>(user32, "DispatchMessageW"),
                DestroyWindow = Export<DestroyWindowFn>(user32, "DestroyWindow"),
                GetDc = Export<GetDcFn>(user32, "GetDC"),
                ReleaseDc = Export<ReleaseDcFn>(user32, "ReleaseDC"),
                ModuleHandle = Export<ModuleHandleFn>(user32, "GetModuleHandleW"),
                LoadCursor = Export<LoadCursorFn>(user32, "LoadCursorW"),
                GetClientRect = Export<GetClientRectFn>(user32, "GetClientRect"),
                AdjustWindowRect = Export<AdjustWindowRectFn>(user32, "AdjustWindowRect"),
                StretchDib = Export<StretchDibFn>(gdi32, "StretchDIBits"),
            };

            if (kernel32 != IntPtr.Zero)
            {
                api.Sleep = Export<SleepFn>(kernel32, "Sleep");
                api.Widen = Export<MultiByteToWideCharFn>(kernel32, "MultiByteToWideChar");
            }

            if (api.RegisterClass is null || api.CreateWindow is null || api.DefWindowProc is null ||
                api.PeekMessage is null || api.DispatchMessage is null || api.GetDc is null ||
                api.StretchDib is null)
            {
                return null;
            }

            _api = api;
            return api;
        }

        // The window procedure has to be a real function pointer, so the delegate
        // is held here for the lifetime of the process: a collected delegate is a
        // crash the first time Windows calls it.
        private static readonly WindowProcFn ProcDelegate = WindowProc;
        private static readonly Dictionary<IntPtr, D3D12Window> Live = new();

        private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
        {
            if (Live.TryGetValue(hwnd, out var window))
            {
                if (message == WmClose || message == WmDestroy ||
                    (message == WmKeyDown && (ulong)wparam == VkEscape))
                {
                    window.Closed = true;
                    if (message != WmDestroy)
                    {
                        _api?.DestroyWindow?.Invoke(hwnd);
                    }

                    return IntPtr.Zero;
                }
            }

            return _api?.DefWindowProc?.Invoke(hwnd, message, wparam, lparam) ?? IntPtr.Zero;
        }

        /// <summary>
        /// True once the window has gone. Read by <c>d3d12.present</c>, which
        /// must stop presenting into a window that is no longer there.
        /// </summary>
        internal bool Closed;

        public static D3D12Window? Create(int width, int height, string title)
        {
            var api = Load();
            if (api is null)
            {
                return null;
            }

            var instance = api.ModuleHandle?.Invoke(IntPtr.Zero) ?? IntPtr.Zero;
            if (!_classRegistered)
            {
                if (_className == IntPtr.Zero)
                {
                    _className = Marshal.StringToHGlobalUni("TERNLangWindow");
                }

                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    Proc = Marshal.GetFunctionPointerForDelegate(ProcDelegate),
                    Instance = instance,
                    Cursor = api.LoadCursor?.Invoke(IntPtr.Zero, IdiArrow) ?? IntPtr.Zero,
                    ClassName = _className,
                };

                if (api.RegisterClass!(ref windowClass) == 0)
                {
                    return null;
                }

                _classRegistered = true;
            }

            var frame = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
            api.AdjustWindowRect?.Invoke(ref frame, WsOverlappedWindow, false);

            var titlePointer = Marshal.StringToHGlobalUni(title);
            try
            {
                var hwnd = api.CreateWindow!(0, _className, titlePointer, WsOverlappedWindow,
                                             unchecked((int)0x80000000), unchecked((int)0x80000000),
                                             frame.Right - frame.Left, frame.Bottom - frame.Top,
                                             IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (hwnd == IntPtr.Zero)
                {
                    return null;
                }

                var window = new D3D12Window(hwnd, width, height);
                Live[hwnd] = window;
                api.ShowWindow?.Invoke(hwnd, SwShow);
                api.UpdateWindow?.Invoke(hwnd);
                return window;
            }
            finally
            {
                Marshal.FreeHGlobal(titlePointer);
            }
        }

        /// <summary>
        /// Shows one frame: <paramref name="rgba"/> is bottom row first, the
        /// layout <c>d3d12.pixels()</c> returns. Returns false once the window
        /// has been closed.
        /// </summary>
        public bool Present(byte[] rgba, int width, int height)
        {
            var api = Load();
            if (api is null || Closed)
            {
                return false;
            }

            if (_dibWidth != width || _dibHeight != height || _dib.Length == 0)
            {
                _dib = new byte[(long)width * height * 4];
                _dibWidth = width;
                _dibHeight = height;
            }

            // A 32-bit BI_RGB bitmap is BGRA in memory and, with a negative
            // height, its first row is the top one.
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var source = (height - 1 - y) * rowBytes;
                var line = y * rowBytes;
                for (var x = 0; x < width; x++)
                {
                    var at = line + x * 4;
                    _dib[at] = rgba[source + x * 4 + 2];
                    _dib[at + 1] = rgba[source + x * 4 + 1];
                    _dib[at + 2] = rgba[source + x * 4];
                    _dib[at + 3] = 255;
                }
            }

            var info = new BitmapInfo
            {
                Header = new BitmapHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    Bits = 32,
                    Compression = BiRgb,
                },
            };

            var clientWidth = width;
            var clientHeight = height;
            if (api.GetClientRect is not null && api.GetClientRect(_hwnd, out var client))
            {
                if (client.Right - client.Left > 0 && client.Bottom - client.Top > 0)
                {
                    clientWidth = client.Right - client.Left;
                    clientHeight = client.Bottom - client.Top;
                }
            }

            var dc = api.GetDc!(_hwnd);
            if (dc != IntPtr.Zero)
            {
                var handle = GCHandle.Alloc(_dib, GCHandleType.Pinned);
                try
                {
                    api.StretchDib!(dc, 0, 0, clientWidth, clientHeight, 0, 0, width, height,
                                    handle.AddrOfPinnedObject(), ref info, DibRgbColors, SrcCopy);
                }
                finally
                {
                    handle.Free();
                }

                api.ReleaseDc?.Invoke(_hwnd, dc);
            }

            Pump(api);
            api.Sleep?.Invoke(16);
            return !Closed;
        }

        private void Pump(Api api)
        {
            var message = default(Msg);
            while (api.PeekMessage!(ref message, IntPtr.Zero, 0, 0, PmRemove))
            {
                if (message.Message == WmQuit)
                {
                    Closed = true;
                    break;
                }

                api.TranslateMessage?.Invoke(ref message);
                api.DispatchMessage?.Invoke(ref message);
            }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                Live.Remove(_hwnd);
                _api?.DestroyWindow?.Invoke(_hwnd);
            }

            Closed = true;
        }
    }

    /// <summary>
    /// The handles <c>d3d12.window</c> hands out, mapped onto the windows that
    /// back them, so a script never holds a raw pointer.
    /// </summary>
    internal sealed class D3D12Windows
    {
        private const int MaxWindows = 4;
        private readonly Dictionary<int, D3D12Window> _windows = new();
        private int _next = 1;

        public int Add(VM vm, D3D12Window window)
        {
            if (_windows.Count >= MaxWindows)
            {
                window.Dispose();
                throw vm.Error("D3D12Error", $"too many windows ({MaxWindows})");
            }

            var handle = _next++;
            _windows[handle] = window;
            return handle;
        }

        public D3D12Window Get(VM vm, in Value value)
        {
            if (value.Kind != ValueKind.Int)
            {
                throw vm.Error("TypeError", $"expected a window handle, not {vm.TypeName(value)}");
            }

            var handle = (int)value.AsInt();
            if (_windows.TryGetValue(handle, out var window))
            {
                return window;
            }

            throw vm.Error("D3D12Error", $"no open window with handle {handle}");
        }

        public void Remove(VM vm, in Value value)
        {
            var window = Get(vm, value);
            _windows.Remove((int)value.AsInt());
            window.Dispose();
        }
    }
}
