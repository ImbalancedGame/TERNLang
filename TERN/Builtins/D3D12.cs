using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// <c>d3d12</c>: Direct3D 12, called through the COM vtables the way the
    /// Windows SDK declares them. There are no interop assemblies and no NuGet
    /// packages: <c>d3d12.dll</c> is opened at run time, the device is created
    /// through <c>D3D12CreateDevice</c>, and every method is a call through the
    /// interface's vtable at the slot the header gives it.
    ///
    /// The structures below are laid out by hand. Their sizes and field offsets
    /// were printed from the shipped <c>d3d12.h</c> (<c>sizeof(D3D12_RESOURCE_DESC)</c>
    /// is 56 bytes, <c>D3D12_RESOURCE_BARRIER</c> is 32, and so on), and the
    /// native backend uses the same numbers, so a mistake shows up as a failing
    /// test against the mock rather than as a corrupted heap.
    ///
    /// The library name comes from <c>TERN_D3D12_LIBRARY</c> when it is set, which
    /// is how the tests run on a machine without Windows: the mock in
    /// <c>tools/d3d12/mock_d3d12.c</c> exports the same entry points.
    /// </summary>
    internal static class D3D12Module
    {
        private const int MaxDevices = 4;
        private const int MaxDimension = 16384;
        private const int WaitTimeout = 30000;

        private const int HeapTypeDefault = 1;
        private const int HeapTypeReadback = 3;
        private const int DescriptorHeapTypeRtv = 2;
        private const int DimensionBuffer = 1;
        private const int DimensionTexture2D = 3;
        private const int StateCommon = 0;
        private const int StateRenderTarget = 4;
        private const int StateCopyDest = 1024;
        private const int StateCopySource = 2048;
        private const int FormatRgba8 = 28;
        private const int CopyTypeSubresource = 0;
        private const int CopyTypePlacedFootprint = 1;
        private const int CommandListTypeDirect = 0;
        private const int FeatureLevel11 = 0xB000;
        private const uint ResourceFlagAllowRenderTarget = 1;
        private const int FormatR32G32B32Float = 6;
        private const int FormatR32G32B32A32Float = 2;
        private const int FormatUnknown = 0;
        private const int HeapTypeUpload = 2;
        private const int StateGenericRead = 2755;
        private const int TopologyTypeTriangle = 3;
        private const int TopologyTriangleList = 4;
        private const int RootSignatureVersion1 = 1;
        private const uint RootSignatureFlagAllowInputAssembler = 1;
        private const int FillModeSolid = 3;
        private const int CullModeNone = 1;
        private const int ComparisonAlways = 8;
        private const int BlendOne = 2;
        private const int BlendOpAdd = 1;
        private const int LogicOpNoop = 4;
        private const int VertexFloats = 7;

        // The swap chain. A back buffer is created in PRESENT, must be moved to
        // COPY_DEST to receive the frame, and must be back in PRESENT before it
        // is shown - Present fails otherwise.
        private const int StatePresent = 0;
        private const int SwapChainBuffers = 2;
        private const uint UsageRenderTargetOutput = 0x20;
        private const int ScalingStretch = 0;
        private const int SwapEffectFlipDiscard = 4;
        private const int AlphaModeIgnore = 3;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();
            var targets = new Targets();
            var windows = new D3D12Windows();

            Add(map, "available", 0, 0, (vm, a) => Value.Bool(Driver.Load() is not null),
                "True when d3d12.dll could be loaded (or TERN_D3D12_LIBRARY points at one).");

            Add(map, "debug", 0, 1, (vm, a) =>
            {
                if (a.Length > 0)
                {
                    if (a[0].Kind != ValueKind.Bool)
                    {
                        throw vm.Error("TypeError", $"d3d12.debug expects true or false, not {vm.TypeName(a[0])}");
                    }

                    _debugWanted = a[0].AsBool();
                    if (_debugWanted)
                    {
                        EnableDebugLayer();
                    }
                }

                if (_debugProblem.Length != 0)
                {
                    // A machine without the layer is a fact, not an error: say so
                    // once, in words that can be acted on, and answer false.
                    Console.Error.WriteLine($"d3d12: validation is off: {_debugProblem}");
                }

                return Value.Bool(_debugOn);
            }, "Turns the Direct3D 12 debug layer on (call before create) and reports whether it is running.");

            Add(map, "info", 0, 0, (vm, a) =>
            {
                var driver = Driver.Load() ?? throw vm.Error("D3D12Error", Unavailable);
                var report = new TernMap();
                report.Set(Value.Str("name"), Value.Str(driver.AdapterName()));
                report.Set(Value.Str("nodes"), new Value(1));
                report.Set(Value.Str("build"), Value.Str(Build));
                return Value.Of(report);
            }, "Describes the adapter: {name, nodes, build}.");

            Add(map, "create", 2, 2, (vm, a) =>
            {
                var width = Dimension(vm, a[0], "width");
                var height = Dimension(vm, a[1], "height");
                return new Value(targets.Add(vm, Target.Create(vm, width, height)));
            }, "Creates an offscreen device and render target, returning a handle.");

            Add(map, "clear", 5, 5, (vm, a) =>
            {
                var target = targets.Get(vm, a[0]);
                target.Clear(vm,
                    (float)Unit(vm, a[1], "red"), (float)Unit(vm, a[2], "green"),
                    (float)Unit(vm, a[3], "blue"), (float)Unit(vm, a[4], "alpha"));
                return Value.Nil;
            }, "Clears the render target: d3d12.clear(handle, r, g, b, a), each 0..1.");

            Add(map, "pipeline", 3, 3, (vm, a) =>
            {
                var target = targets.Get(vm, a[0]);
                var vertex = Shader(vm, a[1], "vertex shader");
                var pixel = Shader(vm, a[2], "pixel shader");
                return new Value(target.MakeProgram(vm, vertex, pixel));
            }, "Compiles HLSL and builds a pipeline: d3d12.pipeline(handle, vertex, pixel).");

            Add(map, "draw", 3, 3, (vm, a) =>
            {
                var target = targets.Get(vm, a[0]);
                var program = Program(vm, target, a[1]);
                var vertices = Vertices(vm, a[2]);
                target.Draw(vm, program, vertices);
                return Value.Nil;
            }, "Draws triangles: vertices are x, y, r, g, b, a floats, three per triangle.");

            Add(map, "pixels", 1, 1, (vm, a) =>
            {
                var target = targets.Get(vm, a[0]);
                var bytes = target.ReadPixels(vm);
                var list = new TernList(bytes.Length);
                foreach (var b in bytes)
                {
                    list.Add(new Value(b));
                }

                return Value.Of(list);
            }, "Reads the target back as RGBA bytes, bottom row first, like gl.pixels.");

            Add(map, "save", 2, 2, (vm, a) =>
            {
                var target = targets.Get(vm, a[0]);
                var path = OutputPath(vm, a[1]);
                var bytes = target.ReadPixels(vm, "d3d12.save");
                long written;
                try
                {
                    written = Png.Write(path, target.Width, target.Height, bytes, flip: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                  or NotSupportedException or ArgumentException)
                {
                    throw vm.Error("IOError", $"cannot write '{path}': {error.Message}");
                }

                return new Value(written);
            }, "Saves the target as a PNG: d3d12.save(handle, path).");

            Add(map, "size", 1, 1, (vm, a) =>
            {
                var target = targets.Get(vm, a[0]);
                var list = new TernList(2);
                list.Add(new Value(target.Width));
                list.Add(new Value(target.Height));
                return Value.Of(list);
            }, "The [width, height] of a device.");

            Add(map, "close", 1, 1, (vm, a) =>
            {
                targets.Remove(vm, a[0]);
                return Value.Nil;
            }, "Releases the device and everything it owns.");

            Add(map, "window", 3, 3, (vm, a) =>
            {
                var width = Dimension(vm, a[0], "width");
                var height = Dimension(vm, a[1], "height");
                if (a[2].Kind != ValueKind.Str)
                {
                    throw vm.Error("TypeError", $"title must be a string, got {vm.TypeName(a[2])}");
                }

                // nil, not an error: a machine with no window layer can still
                // use everything else in this module.
                var window = D3D12Window.Create(width, height, a[2].AsString());
                return window is null ? Value.Nil : new Value(windows.Add(vm, window));
            }, "Opens a window to show frames in: d3d12.window(width, height, title), or nil.");

            Add(map, "present", 2, 2, (vm, a) =>
            {
                var window = windows.Get(vm, a[0]);
                var target = targets.Get(vm, a[1]);

                // A closed window takes no more frames: the blit path always
                // said so, and the swap chain path has to agree with it.
                if (window.Closed)
                {
                    return Value.Bool(false);
                }

                // The swap chain first: it hands the frame to the compositor
                // without a trip through system memory or a channel swap. The
                // blit below is what a machine without IDXGIFactory2 gets.
                if (target.TryPresent(vm, window.Handle))
                {
                    return Value.Bool(window.PumpMessages());
                }

                var pixels = target.ReadPixels(vm, "d3d12.present");
                return Value.Bool(window.Present(pixels, target.Width, target.Height));
            }, "Shows the target in the window: d3d12.present(window, device); false once it is closed.");

            Add(map, "closeWindow", 1, 1, (vm, a) =>
            {
                windows.Remove(vm, a[0]);
                return Value.Nil;
            }, "Closes a window and forgets its handle.");

            engine.RegisterValue("d3d12", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:d3d12",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        private const string Unavailable =
            "Direct3D 12 is not available on this system: the Direct3D 12 library could not be loaded";

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string documentation) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        // ------------------------------------------------------------------ arguments

        private static int Dimension(VM vm, in Value value, string what)
        {
            if (value.Kind != ValueKind.Int)
            {
                throw vm.Error("TypeError", $"{what} must be an integer, not {vm.TypeName(value)}");
            }

            var size = (int)value.AsInt();
            if (size < 1 || size > MaxDimension)
            {
                throw vm.Error("ArgumentError", $"{what} must be between 1 and {MaxDimension}");
            }

            return size;
        }

        private static double Unit(VM vm, in Value value, string what)
        {
            if (!value.IsNumber)
            {
                throw vm.Error("TypeError", $"{what} must be a number, not {vm.TypeName(value)}");
            }

            var number = value.AsDouble();
            if (number < 0 || number > 1)
            {
                throw vm.Error("ArgumentError", $"{what} must be between 0 and 1");
            }

            return number;
        }

        private static string Shader(VM vm, in Value value, string what)
        {
            if (value.Kind != ValueKind.Str)
            {
                throw vm.Error("TypeError", $"{what} must be a string, not {vm.TypeName(value)}");
            }

            return value.AsString();
        }

        private static int Program(VM vm, Target target, in Value value)
        {
            if (value.Kind != ValueKind.Int)
            {
                throw vm.Error("TypeError", $"expected a pipeline handle, not {vm.TypeName(value)}");
            }

            var handle = (int)value.AsInt();
            if (handle < 1 || handle > target.ProgramCount)
            {
                throw vm.Error("D3D12Error", $"no pipeline with handle {handle}");
            }

            return handle;
        }

        private static float[] Vertices(VM vm, in Value value)
        {
            if (value.Kind != ValueKind.List)
            {
                throw vm.Error("TypeError", $"expected a list of vertices, not {vm.TypeName(value)}");
            }

            var list = value.AsList();
            if (list.Count % 6 != 0)
            {
                throw vm.Error("ArgumentError",
                    $"vertices must be groups of 6 numbers (x, y, r, g, b, a), not {list.Count}");
            }

            var vertices = new float[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].IsNumber)
                {
                    throw vm.Error("TypeError", $"vertex {i} must be a number, not {vm.TypeName(list[i])}");
                }

                vertices[i] = (float)list[i].AsDouble();
            }

            return vertices;
        }

        private static string OutputPath(VM vm, in Value value)
        {
            var engine = vm.Engine ?? throw vm.Error("InternalError", "no engine attached to this VM");
            if (!engine.AllowFileSystem)
            {
                throw vm.Error("PermissionError", "file system access is disabled for this script");
            }

            if (value.Kind != ValueKind.Str)
            {
                throw vm.Error("TypeError", $"path must be a string, got {vm.TypeName(value)}");
            }

            var path = value.AsString();
            // Same rule as fs: a relative destination is relative to where the
            // program was started, which is also what a compiled binary does.
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        }

        // ------------------------------------------------------------------- structures

        [StructLayout(LayoutKind.Sequential)]
        private struct HeapProperties
        {
            public int Type;
            public int CpuPageProperty;
            public int MemoryPoolPreference;
            public uint CreationNodeMask;
            public uint VisibleNodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SampleDesc
        {
            public uint Count;
            public uint Quality;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ResourceDesc
        {
            public int Dimension;
            public ulong Alignment;
            public ulong Width;
            public uint Height;
            public ushort DepthOrArraySize;
            public ushort MipLevels;
            public int Format;
            public SampleDesc SampleDesc;
            public int Layout;
            public int Flags;
        }

        /// <summary>
        /// D3D12_CLEAR_VALUE: the colour a render target is optimised for.
        /// </summary>
        /// <remarks>
        /// Passing NULL instead is legal, but the debug layer warns that every
        /// clear is then ordered against the previous contents. Handing the
        /// device the colour up front is what stops that warning, and it is the
        /// same value the clear call uses.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct ClearValue
        {
            public int Format;
            public float R;
            public float G;
            public float B;
            public float A;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DescriptorHeapDesc
        {
            public int Type;
            public uint NumDescriptors;
            public uint Flags;
            public uint NodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TransitionResource
        {
            public IntPtr Resource;
            public uint Subresource;
            public uint StateBefore;
            public uint StateAfter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ResourceBarrier
        {
            public int Type;
            public uint Flags;
            public TransitionResource Transition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Range
        {
            public ulong Begin;
            public ulong End;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SubresourceFootprint
        {
            public int Format;
            public uint Width;
            public uint Height;
            public uint Depth;
            public uint RowPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PlacedFootprint
        {
            public ulong Offset;
            public SubresourceFootprint Footprint;
        }

        /// <summary>
        /// DXGI_SWAP_CHAIN_DESC1: 48 bytes, in the order dxgi1_2.h lays it out.
        /// The native runtime asserts the same offsets, and
        /// tools/d3d12/gen_layout_check.py checks them against the SDK.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SwapChainDesc1
        {
            public uint Width;
            public uint Height;
            public int Format;
            public int Stereo;
            public SampleDesc Sample;
            public uint BufferUsage;
            public uint BufferCount;
            public int Scaling;
            public int SwapEffect;
            public int AlphaMode;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct TextureCopyLocation
        {
            [FieldOffset(0)] public IntPtr Resource;
            [FieldOffset(8)] public int Type;
            [FieldOffset(16)] public PlacedFootprint PlacedFootprint;
            [FieldOffset(16)] public uint SubresourceIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderBytecode
        {
            public IntPtr Bytecode;
            public IntPtr Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StreamOutput
        {
            public IntPtr Declarations;
            public uint Entries;
            public IntPtr Strides;
            public uint StrideCount;
            public uint RasterizedStream;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RenderTargetBlend
        {
            public int BlendEnable;
            public int LogicOpEnable;
            public int SrcBlend;
            public int DestBlend;
            public int BlendOp;
            public int SrcBlendAlpha;
            public int DestBlendAlpha;
            public int BlendOpAlpha;
            public int LogicOp;
            public byte WriteMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] Padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlendDesc
        {
            public int AlphaToCoverage;
            public int IndependentBlend;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public RenderTargetBlend[] Targets;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RasterizerDesc
        {
            public int FillMode;
            public int CullMode;
            public int FrontCounterClockwise;
            public int DepthBias;
            public float DepthBiasClamp;
            public float SlopeScaledDepthBias;
            public int DepthClipEnable;
            public int MultisampleEnable;
            public int AntialiasedLineEnable;
            public uint ForcedSampleCount;
            public int ConservativeRaster;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StencilOp
        {
            public int StencilFailOp;
            public int StencilDepthFailOp;
            public int StencilPassOp;
            public int StencilFunc;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DepthStencilDesc
        {
            public int DepthEnable;
            public int DepthWriteMask;
            public int DepthFunc;
            public int StencilEnable;
            public byte StencilReadMask;
            public byte StencilWriteMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] Padding;
            public StencilOp FrontFace;
            public StencilOp BackFace;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CachedPipelineState
        {
            public IntPtr Blob;
            public IntPtr Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InputElement
        {
            [MarshalAs(UnmanagedType.LPStr)] public string SemanticName;
            public uint SemanticIndex;
            public int Format;
            public uint InputSlot;
            public uint AlignedByteOffset;
            public int InputSlotClass;
            public uint InstanceDataStepRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InputLayoutDesc
        {
            public IntPtr Elements;
            public uint Count;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GraphicsPipelineStateDesc
        {
            public IntPtr RootSignature;
            public ShaderBytecode Vs;
            public ShaderBytecode Ps;
            public ShaderBytecode Ds;
            public ShaderBytecode Hs;
            public ShaderBytecode Gs;
            public StreamOutput StreamOutput;
            public BlendDesc BlendState;
            public uint SampleMask;
            public RasterizerDesc RasterizerState;
            public DepthStencilDesc DepthStencilState;
            public InputLayoutDesc InputLayout;
            public int IbStripCutValue;
            public int PrimitiveTopologyType;
            public uint NumRenderTargets;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] RtvFormats;
            public int DsvFormat;
            public SampleDesc SampleDesc;
            public uint NodeMask;
            public CachedPipelineState CachedPso;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RootSignatureDesc
        {
            public uint NumParameters;
            public IntPtr Parameters;
            public uint NumStaticSamplers;
            public IntPtr StaticSamplers;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VertexBufferView
        {
            public ulong BufferLocation;
            public uint SizeInBytes;
            public uint StrideInBytes;
        }

        /// <summary>
        /// D3D12_VIEWPORT: where the rasteriser is allowed to draw.
        /// </summary>
        /// <remarks>
        /// Direct3D 12 has no default viewport. Until one is set the viewport
        /// list is empty and every triangle is discarded, while a clear - which
        /// does not consult it - still fills the target. A window showing the
        /// clear colour and nothing else is what that looks like.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct Viewport
        {
            public float TopLeftX;
            public float TopLeftY;
            public float Width;
            public float Height;
            public float MinDepth;
            public float MaxDepth;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuDescriptorHandle
        {
            public IntPtr Pointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CommandQueueDesc
        {
            public int Type;
            public int Priority;
            public uint Flags;
            public uint NodeMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Guid
        {
            public uint A;
            public ushort B;
            public ushort C;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] D;

            public static Guid Of(uint a, ushort b, ushort c, params byte[] d) =>
                new Guid { A = a, B = b, C = c, D = d };

            public IntPtr Allocate()
            {
                var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
                Marshal.StructureToPtr(this, pointer, false);
                return pointer;
            }

            /// <summary>Runs <paramref name="body"/> with this id, then frees it.</summary>
            public T With<T>(Func<IntPtr, T> body)
            {
                var pointer = Allocate();
                try
                {
                    return body(pointer);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }

        /// <summary>
        /// The revision of this binding, reported by <c>d3d12.info()</c> and
        /// printed by <c>examples/rotating_cube.tern</c>. It exists because a
        /// report of "the readback buffer could not be mapped (0x887a0005)" was
        /// traced to a binary built before the fix that caused it: without a
        /// stamp the two look identical, and a fix that worked looks like one
        /// that did not. Keep it equal to <c>TERN_D3D12_BUILD</c>.
        /// </summary>
        private const string Build = "2026-10-31.9";

        /// <summary>
        /// Why the driver removed the device, in the words the native backend
        /// uses, or an empty string when it did not. The two tables have to
        /// match: both back ends report the same failures.
        /// </summary>
        /// <summary>
        /// What a GetDeviceRemovedReason code means. The raw code is kept on
        /// every message: "the device was removed for an unknown reason" was all
        /// one report said about a machine with a real GPU in it, and the code
        /// is the difference between guessing and knowing. Same table as
        /// <c>d3d12_removal_text</c> in the native runtime.
        /// </summary>
        // --- the debug layer -------------------------------------------------
        //
        // D3D12 can validate itself. With the layer on, the driver says exactly
        // which call was illegal and why, which is the only way to fix a failure
        // on a machine nobody here can run: "the device was removed for an
        // unknown reason" cannot be acted on, and "CopyTextureRegion: resource
        // state is invalid" can.
        //
        // Off by default - the layer is slower, and it needs the "Graphics
        // Tools" optional Windows feature. d3d12.debug(true) turns it on and has
        // to run before d3d12.create: a device cannot be validated afterwards.

        private static bool _debugWanted;
        private static bool _debugOn;
        private static string _debugProblem = "";

        internal static void ResetDebug()
        {
            _debugWanted = false;
            _debugOn = false;
            _debugProblem = "";
        }

        private static readonly Guid IidDebug =
            Guid.Of(0x344488b7, 0x6846, 0x474b, 0xb9, 0x89, 0xf0, 0x27, 0x44, 0x82, 0x45, 0xe0);

        private static readonly Guid IidInfoQueue =
            Guid.Of(0x0742a90b, 0xc387, 0x483f, 0xb9, 0x46, 0x30, 0xa7, 0xe4, 0xe6, 0x14, 0x58);

        private static IntPtr OpenInfoQueue(IntPtr device)
        {
            var iid = IidInfoQueue.Allocate();
            try
            {
                var hr = Slot<QueryInterfaceFn>(device, 0)(device, iid, out var queue);
                return hr >= 0 ? queue : IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(iid);
            }
        }

        private static string SeverityName(int severity) => severity switch
        {
            0 => "CORRUPTION",
            1 => "ERROR",
            2 => "WARNING",
            3 => "INFO",
            4 => "MESSAGE",
            _ => "UNKNOWN",
        };

        private static bool EnableDebugLayer()
        {
            if (_debugOn)
            {
                return true;
            }
            if (_debugProblem.Length != 0)
            {
                return false;
            }
            var getDebugInterface = GetDebugInterface();
            if (getDebugInterface == null)
            {
                _debugProblem = "D3D12GetDebugInterface could not be found in d3d12.dll";
                return false;
            }
            var iid = IidDebug.Allocate();
            IntPtr debug;
            long hr;
            try
            {
                hr = getDebugInterface(iid, out debug);
            }
            finally
            {
                Marshal.FreeHGlobal(iid);
            }
            if (hr < 0 || debug == IntPtr.Zero)
            {
                _debugProblem = "the debug layer is not installed: Windows Settings > " +
                                "Apps > Optional features > Graphics Tools";
                return false;
            }
            try
            {
                Slot<EnableDebugLayerFn>(debug, 3)(debug);
            }
            finally
            {
                Release(debug);
            }
            _debugOn = true;
            return true;
        }

        /// <summary>Prints and clears the driver's stored validation messages.</summary>
        private static void ReportMessages(IntPtr queue)
        {
            if (queue == IntPtr.Zero)
            {
                return;
            }
            var count = Slot<GetNumStoredMessagesFn>(queue, 8)(queue);
            for (ulong i = 0; i < count; i++)
            {
                nuint size = 0;
                var getMessage = Slot<GetMessageFn>(queue, 5);
                if (getMessage(queue, i, IntPtr.Zero, ref size) < 0 || size == 0)
                {
                    break;
                }
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (getMessage(queue, i, buffer, ref size) >= 0)
                    {
                        var message = Marshal.PtrToStructure<DebugMessage>(buffer);
                        if (message.Description != IntPtr.Zero)
                        {
                            var text = Marshal.PtrToStringAnsi(message.Description) ?? "";
                            Console.Error.WriteLine($"d3d12 debug: {SeverityName(message.Severity)}: {text}");
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            Slot<ClearStoredMessagesFn>(queue, 4)(queue);
        }

        private static string RemovalText(uint code)
        {
            return code switch
            {
                0 => string.Empty,
                0x887A0001 => "a command was rejected as invalid",
                0x887A0005 => "the device was removed",
                0x887A0006 => "the GPU hung and the driver reset it (TDR)",
                0x887A0007 => "the device was reset",
                0x887A000A => "the driver compiled a shader incorrectly",
                0x887A0020 => "the driver hit an internal error",
                0x887A002B => "the driver ran out of memory",
                0x8007000E => "out of memory",
                _ => "the device was removed for an unknown reason",
            };
        }

        private static string RemovedReason(IntPtr device)
        {
            if (device == IntPtr.Zero)
            {
                return string.Empty;
            }

            return RemovalText((uint)Slot<DeviceRemovedReasonFn>(device, 37)(device));
        }

        /// <summary>
        /// The error for a call that failed: the driver's reason when the device
        /// was removed, otherwise what the HRESULT means. Same text as
        /// <c>d3d12_fail_removed</c> in the native runtime.
        /// </summary>
        private static Exception RemovedError(VM vm, IntPtr device, string what, long hr)
        {
            if (device != IntPtr.Zero)
            {
                var code = (uint)Slot<DeviceRemovedReasonFn>(device, 37)(device);
                if (code != 0)
                {
                    return vm.Error("D3D12Error", $"{what}: {RemovalText(code)} (0x{(uint)hr:x8})");
                }
            }

            var explained = Explain(hr);
            return explained.Length != 0
                ? vm.Error("D3D12Error", $"{what}: {explained} (0x{(uint)hr:x8})")
                : vm.Error("D3D12Error", $"{what} (0x{(uint)hr:x8})");
        }

        /// <summary>
        /// What an HRESULT from Direct3D means, in words a person can act on.
        /// The raw code is always kept: it is the only thing that makes a report
        /// precise. The native runtime has the same table, and they are kept in
        /// step because both back ends report the same failures.
        /// </summary>
        private static string Explain(long hr)
        {
            return ((uint)hr) switch
            {
                0x80004002 => "an interface id was rejected - this is a bug in TERN's Direct3D binding, not in this machine",
                0x887A0004 => "no adapter on this machine supports Direct3D 12 at feature level 11_0; a driver update may add it",
                0x887A0005 => "the graphics driver was reset while the call was running",
                0x887A0006 => "the graphics driver stopped responding and was reset",
                0x80070057 => "the description was rejected as invalid - this is a bug in TERN",
                0x8007000E => "out of memory",
                0x80070005 => "access was denied - a remote desktop session or a sandbox without GPU access looks like this",
                _ => null,
            } ?? string.Empty;
        }

        // Every Create* takes the interface id it should hand back and answers
        // E_NOINTERFACE when it does not recognise the one it was given, so
        // passing the device id to CreateCommandQueue fails on hardware. The
        // ids below are the uuid() values from d3d12.idl and dxgi.idl, the same
        // ones the native runtime carries in tern_d3d12.h; the mock driver
        // checks them, which is how a wrong one is caught without a GPU.
        private static readonly Guid IidDevice =
            Guid.Of(0x189819f1, 0x1db6, 0x4b57, 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7);

        private static readonly Guid IidCommandQueue =
            Guid.Of(0x0ec870a6, 0x5d7e, 0x4c22, 0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed);

        private static readonly Guid IidCommandAllocator =
            Guid.Of(0x6102dee4, 0xaf59, 0x4b09, 0xb9, 0x99, 0xb4, 0x4d, 0x73, 0xf0, 0x9b, 0x24);

        private static readonly Guid IidGraphicsCommandList =
            Guid.Of(0x5b160d0f, 0xac1b, 0x4185, 0x8b, 0xa8, 0xb3, 0xae, 0x42, 0xa5, 0xa4, 0x55);

        private static readonly Guid IidDescriptorHeap =
            Guid.Of(0x8efb471d, 0x616c, 0x4f49, 0x90, 0xf7, 0x12, 0x7b, 0xb7, 0x63, 0xfa, 0x51);

        private static readonly Guid IidResource =
            Guid.Of(0x696442be, 0xa72e, 0x4059, 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad);

        private static readonly Guid IidFence =
            Guid.Of(0x0a753dcf, 0xc4d8, 0x4b91, 0xad, 0xf6, 0xbe, 0x5a, 0x60, 0xd9, 0x5a, 0x76);

        private static readonly Guid IidRootSignature =
            Guid.Of(0xc54a6b66, 0x72df, 0x4ee8, 0x8b, 0xe5, 0xa9, 0x46, 0xa1, 0x42, 0x92, 0x14);

        private static readonly Guid IidPipelineState =
            Guid.Of(0x765a30f3, 0xf624, 0x4c6f, 0xa8, 0x28, 0xac, 0xe9, 0x48, 0x62, 0x24, 0x45);

        private static readonly Guid IidFactory1 =
            Guid.Of(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

        // IDXGIFactory2 is where CreateSwapChainForHwnd lives; it arrived with
        // Windows 8, which is why a machine without it falls back to a blit.
        private static readonly Guid IidFactory2 =
            Guid.Of(0x50c83a1c, 0xe072, 0x4c48, 0x87, 0xb0, 0x36, 0x30, 0xfa, 0x36, 0xa6, 0xd0);

        // ---------------------------------------------------------------------- vtables

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateCommandQueueFn(IntPtr self, IntPtr desc, IntPtr iid, out IntPtr queue);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateCommandAllocatorFn(IntPtr self, int type, IntPtr iid, out IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateCommandListFn(IntPtr self, int type, int flags, IntPtr allocator, IntPtr state, IntPtr iid, out IntPtr list);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateDescriptorHeapFn(IntPtr self, ref DescriptorHeapDesc desc, IntPtr iid, out IntPtr heap);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetCpuHandleFn(IntPtr self, out CpuDescriptorHandle handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateCommittedResourceFn(IntPtr self, ref HeapProperties heap, uint flags, ref ResourceDesc desc, int state, IntPtr clear, IntPtr iid, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CreateRenderTargetViewFn(IntPtr self, IntPtr resource, IntPtr desc, CpuDescriptorHandle handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateFenceFn(IntPtr self, ulong value, uint flags, IntPtr iid, out IntPtr fence);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetCopyableFootprintsFn(IntPtr self, ref ResourceDesc desc, uint first, uint count, ulong offset, out PlacedFootprint layout, out uint rows, out ulong rowSize, out ulong total);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong FenceValueFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long ResetAllocatorFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long ResetListFn(IntPtr self, IntPtr allocator, IntPtr state);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CloseListFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ResourceBarrierFn(IntPtr self, uint count, ref ResourceBarrier barrier);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void OMSetRenderTargetsFn(IntPtr self, uint count, ref CpuDescriptorHandle handle, int single, IntPtr depth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearRenderTargetViewFn(IntPtr self, CpuDescriptorHandle handle, ref float colour, uint count, IntPtr rects);
        // CopyTextureRegion takes DstZ between DstY and the source location. Leaving
        // it out is not a compile error - it is a function pointer - and the first
        // real GPU removed the device on the first copy because of it: the driver
        // read the source location as a Z coordinate and the next slot as the
        // source. The native runtime had the same omission in its own typedef.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyTextureRegionFn(IntPtr self, ref TextureCopyLocation destination, uint x, uint y, uint z, ref TextureCopyLocation source, IntPtr box);

        // DXGI. QueryInterface (declared with the debug layer's below) is slot 0
        // of every COM interface, and it is how IDXGIFactory1 becomes the
        // IDXGIFactory2 that can make a swap chain.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateSwapChainForHwndFn(IntPtr self, IntPtr device, IntPtr window, ref SwapChainDesc1 desc, IntPtr fullscreen, IntPtr restrict, out IntPtr chain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long GetBufferFn(IntPtr self, uint index, IntPtr iid, out IntPtr surface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long PresentFn(IntPtr self, uint syncInterval, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ExecuteCommandListsFn(IntPtr self, uint count, IntPtr lists);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long FenceSignalFn(IntPtr self, IntPtr fence, ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long MapFn(IntPtr self, uint subresource, ref Range range, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, uint subresource, ref Range range);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateGraphicsPipelineStateFn(IntPtr self, ref GraphicsPipelineStateDesc desc, IntPtr iid, out IntPtr pipeline);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long CreateRootSignatureFn(IntPtr self, uint nodeMask, IntPtr blob, IntPtr length, IntPtr iid, out IntPtr signature);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetGraphicsRootSignatureFn(IntPtr self, IntPtr signature);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetPipelineStateFn(IntPtr self, IntPtr pipeline);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void IASetPrimitiveTopologyFn(IntPtr self, int topology);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetVertexBuffersFn(IntPtr self, uint slot, uint count, ref VertexBufferView view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewportsFn(IntPtr self, uint count, ref Viewport viewport);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetScissorRectsFn(IntPtr self, uint count, ref Rect rect);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawInstancedFn(IntPtr self, uint vertices, uint instances, uint startVertex, uint startInstance);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong GetBufferSizeFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetBufferPointerFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetGpuAddressFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDesc1Fn(IntPtr self, IntPtr description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long DeviceRemovedReasonFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void EnableDebugLayerFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long QueryInterfaceFn(IntPtr self, IntPtr iid, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long GetDebugInterfaceFn(IntPtr iid, out IntPtr debug);

        /// <summary>
        /// D3D12GetDebugInterface, or null when this d3d12.dll has no such export.
        /// Optional, like the layer itself.
        /// </summary>
        private static GetDebugInterfaceFn? GetDebugInterface()
        {
            var name = Environment.GetEnvironmentVariable("TERN_D3D12_LIBRARY");
            var candidates = string.IsNullOrEmpty(name) ? new[] { "d3d12.dll" } : new[] { name };
            foreach (var candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out var library) &&
                    NativeLibrary.TryGetExport(library, "D3D12GetDebugInterface", out var address))
                {
                    return (GetDebugInterfaceFn)Marshal.GetDelegateForFunctionPointer(
                        address, typeof(GetDebugInterfaceFn));
                }
            }
            return null;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong GetNumStoredMessagesFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long GetMessageFn(IntPtr self, ulong index, IntPtr message, ref nuint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearStoredMessagesFn(IntPtr self);

        [StructLayout(LayoutKind.Sequential)]
        private struct DebugMessage
        {
            public int Category;
            public int Severity;
            public uint Id;
            public IntPtr Description;
            public nuint DescriptionBytes;
        }

        private static T Slot<T>(IntPtr obj, int slot) where T : Delegate
        {
            var vtbl = Marshal.ReadIntPtr(obj);
            var address = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero)
            {
                return;
            }

            Slot<ReleaseFn>(obj, 2)(obj);
        }

        // ----------------------------------------------------------------------- driver

        private sealed class Driver
        {
            private static Driver? _cached;
            private static bool _attempted;

            public delegate long CreateDeviceFn(IntPtr adapter, int level, IntPtr iid, out IntPtr device);
            public delegate long CreateFactoryFn(IntPtr iid, out IntPtr factory);
            public delegate long CompileFn(IntPtr source, IntPtr size, IntPtr name, IntPtr defines,
                                            IntPtr include, IntPtr entry, IntPtr target, uint flags1,
                                            uint flags2, out IntPtr code, out IntPtr errors);
            public delegate long SerializeRootSignatureFn(ref RootSignatureDesc desc, int version,
                                                           out IntPtr blob, out IntPtr errors);

            private IntPtr _d3d12;
            private CreateDeviceFn _createDevice;
            private IntPtr _dxgi;
            private CreateFactoryFn? _createFactory;

            private Driver(IntPtr d3d12, CreateDeviceFn createDevice, IntPtr dxgi, CreateFactoryFn? createFactory)
            {
                _d3d12 = d3d12;
                _createDevice = createDevice;
                _dxgi = dxgi;
                _createFactory = createFactory;
            }

            public static Driver? Load()
            {
                if (_attempted)
                {
                    return _cached;
                }

                _attempted = true;
                var configured = Environment.GetEnvironmentVariable("TERN_D3D12_LIBRARY");
                var names = string.IsNullOrEmpty(configured)
                    ? new[] { "d3d12.dll" }
                    : new[] { configured };

                foreach (var name in names)
                {
                    if (!NativeLibrary.TryLoad(name, out var library))
                    {
                        continue;
                    }

                    if (!NativeLibrary.TryGetExport(library, "D3D12CreateDevice", out var address))
                    {
                        continue;
                    }

                    var createDevice = (CreateDeviceFn)Marshal.GetDelegateForFunctionPointer(address, typeof(CreateDeviceFn));

                    // DXGI is only needed for the adapter name, so it is optional.
                    var dxgiName = Environment.GetEnvironmentVariable("TERN_DXGI_LIBRARY");
                    var candidates = string.IsNullOrEmpty(dxgiName)
                        ? new[] { "dxgi.dll", name }
                        : new[] { dxgiName };

                    IntPtr dxgi = IntPtr.Zero;
                    CreateFactoryFn? createFactory = null;
                    foreach (var candidate in candidates)
                    {
                        if (!NativeLibrary.TryLoad(candidate, out dxgi))
                        {
                            dxgi = IntPtr.Zero;
                            continue;
                        }

                        if (NativeLibrary.TryGetExport(dxgi, "CreateDXGIFactory1", out var factoryAddress))
                        {
                            createFactory = (CreateFactoryFn)Marshal.GetDelegateForFunctionPointer(factoryAddress, typeof(CreateFactoryFn));
                            break;
                        }

                        dxgi = IntPtr.Zero;
                    }

                    var driver = new Driver(library, createDevice, dxgi, createFactory);
                    if (NativeLibrary.TryGetExport(library, "D3D12SerializeRootSignature", out var serialize))
                    {
                        driver.SerializeRootSignature =
                            (SerializeRootSignatureFn)Marshal.GetDelegateForFunctionPointer(serialize, typeof(SerializeRootSignatureFn));
                    }

                    _cached = driver;
                    return _cached;
                }

                return null;
            }

            public SerializeRootSignatureFn? SerializeRootSignature { get; private set; }

            /// <summary>
            /// d3dcompiler_47 is a separate library, opened when the first shader
            /// is compiled so a script that never renders never touches it.
            /// </summary>
            public CompileFn? Compiler()
            {
                if (_compilerLoaded)
                {
                    return _compiler;
                }

                _compilerLoaded = true;
                var configured = Environment.GetEnvironmentVariable("TERN_D3DCOMPILER_LIBRARY");
                var names = string.IsNullOrEmpty(configured)
                    ? new[] { "d3dcompiler_47.dll", "libd3dcompiler_47.so" }
                    : new[] { configured };
                foreach (var name in names)
                {
                    if (!NativeLibrary.TryLoad(name, out var library))
                    {
                        continue;
                    }

                    if (NativeLibrary.TryGetExport(library, "D3DCompile", out var address))
                    {
                        _compiler = (CompileFn)Marshal.GetDelegateForFunctionPointer(address, typeof(CompileFn));
                        return _compiler;
                    }
                }

                return null;
            }

            private bool _compilerLoaded;
            private CompileFn? _compiler;

            public IntPtr CreateDevice(VM vm, int width, int height)
            {
                if (_debugWanted)
                {
                    EnableDebugLayer();
                }

                var iid = IidDevice.Allocate();
                try
                {
                    var hr = _createDevice(IntPtr.Zero, FeatureLevel11, iid, out var device);
                    if (hr < 0 || device == IntPtr.Zero)
                    {
                        var reason = Explain(hr);
                        var detail = reason.Length > 0
                            ? $": {reason} (0x{(uint)hr:x8})"
                            : $" (0x{(uint)hr:x8})";
                        throw vm.Error("D3D12Error",
                            $"Direct3D 12 is not available on this system: D3D12CreateDevice failed{detail}");
                    }

                    return device;
                }
                finally
                {
                    Marshal.FreeHGlobal(iid);
                }
            }

            public string AdapterName()
            {
                if (_createFactory is null)
                {
                    return string.Empty;
                }

                var iid = IidFactory1.Allocate();
                IntPtr adapter = IntPtr.Zero;
                IntPtr factory = IntPtr.Zero;
                try
                {
                    if (_createFactory(iid, out factory) < 0 || factory == IntPtr.Zero)
                    {
                        return string.Empty;
                    }

                    if (Slot<EnumAdapters1Fn>(factory, 12)(factory, 0, out adapter) < 0 || adapter == IntPtr.Zero)
                    {
                        return string.Empty;
                    }

                    var description = Marshal.AllocHGlobal(320);
                    try
                    {
                        for (var i = 0; i < 320; i++)
                        {
                            Marshal.WriteByte(description, i, 0);
                        }

                        Slot<GetDesc1Fn>(adapter, 10)(adapter, description);
                        var builder = new StringBuilder();
                        for (var i = 0; i < 128; i++)
                        {
                            var code = (char)Marshal.ReadInt16(description, i * 2);
                            if (code == '\0')
                            {
                                break;
                            }

                            // The native backend copies the BMP only; anything above
                            // ASCII becomes a question mark so both agree.
                            builder.Append(code < 128 ? code : '?');
                        }

                        return builder.ToString();
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(description);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(iid);
                    Release(adapter);
                    Release(factory);
                }
            }

            /// <summary>
            /// An IDXGIFactory1, or <see cref="IntPtr.Zero"/> when this machine
            /// has no DXGI. The caller releases it.
            /// </summary>
            public IntPtr CreateFactory()
            {
                if (_createFactory is null)
                {
                    return IntPtr.Zero;
                }

                var iid = IidFactory1.Allocate();
                try
                {
                    return _createFactory(iid, out var factory) < 0 ? IntPtr.Zero : factory;
                }
                finally
                {
                    Marshal.FreeHGlobal(iid);
                }
            }

            public int Pid => 0;
        }

        // ----------------------------------------------------------------------- target

        private sealed class Target : IDisposable
        {
            private IntPtr _device;
            private IntPtr _queue;
            private IntPtr _allocator;
            private IntPtr _list;
            private IntPtr _heap;
            private uint _state = StateCommon;
            private IntPtr _texture;
            private IntPtr _readback;
            private IntPtr _fence;
            private IntPtr _infoQueue;
            private CpuDescriptorHandle _rtv;
            private ulong _fenceValue;
            private ulong _rowPitch;
            private ulong _totalBytes;
            private readonly List<IntPtr> _pipelines = new();
            private readonly List<IntPtr> _signatures = new();
            private IntPtr _vertexBuffer;

            // The swap chain, if this target has been shown in a window. It is
            // created once and kept until the target or the window goes away.
            private IntPtr _swap;
            private readonly IntPtr[] _swapBuffers = new IntPtr[SwapChainBuffers];
            private readonly uint[] _swapStates = new uint[SwapChainBuffers];
            private IntPtr _swapWindow;
            private int _swapFrame;
            private bool _swapFailed;

            public int ProgramCount => _pipelines.Count;

            private Target(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }

            public static Target Create(VM vm, int width, int height)
            {
                var driver = Driver.Load() ?? throw vm.Error("D3D12Error", Unavailable);
                var target = new Target(width, height);
                target._device = driver.CreateDevice(vm, width, height);
                // Validation messages are stored per device, so the queue is opened
                // as soon as there is a device to ask.
                target._infoQueue = _debugOn ? OpenInfoQueue(target._device) : IntPtr.Zero;
                target.Prepare(vm);
                return target;
            }

            private void Prepare(VM vm)
            {
                var queueDesc = new CommandQueueDesc { Type = CommandListTypeDirect };
                var queueDescPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CommandQueueDesc>());
                try
                {
                    Marshal.StructureToPtr(queueDesc, queueDescPointer, false);
                    if (IidCommandQueue.With(iid =>
                        Slot<CreateCommandQueueFn>(_device, 8)(_device, queueDescPointer, iid, out _queue)) < 0)
                    {
                        throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a command queue could not be created");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(queueDescPointer);
                }

                if (IidCommandAllocator.With(iid =>
                    Slot<CreateCommandAllocatorFn>(_device, 9)(_device, CommandListTypeDirect, iid, out _allocator)) < 0)
                {
                    throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a command allocator could not be created");
                }

                if (IidGraphicsCommandList.With(iid =>
                    Slot<CreateCommandListFn>(_device, 12)(_device, CommandListTypeDirect, 0, _allocator, IntPtr.Zero, iid, out _list)) < 0)
                {
                    throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a command list could not be created");
                }

                Slot<CloseListFn>(_list, 9)(_list);

                var heapDesc = new DescriptorHeapDesc { Type = DescriptorHeapTypeRtv, NumDescriptors = 1 };
                if (IidDescriptorHeap.With(iid =>
                    Slot<CreateDescriptorHeapFn>(_device, 14)(_device, ref heapDesc, iid, out _heap)) < 0)
                {
                    throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a descriptor heap could not be created");
                }

                Slot<GetCpuHandleFn>(_heap, 9)(_heap, out _rtv);

                var texture = new ResourceDesc
                {
                    Dimension = DimensionTexture2D,
                    Width = (ulong)Width,
                    Height = (uint)Height,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = FormatRgba8,
                    SampleDesc = new SampleDesc { Count = 1 },
                    Flags = (int)ResourceFlagAllowRenderTarget,
                };

                var heap = new HeapProperties { Type = HeapTypeDefault, CreationNodeMask = 1 };

                // The colour the target is cleared to, told to the device when
                // the resource is made: without it the debug layer warns, and
                // it is right - a clear the driver knows about in advance can
                // replace the contents instead of being ordered against them.
                var clear = new ClearValue { Format = FormatRgba8, R = 0.05f, G = 0.06f, B = 0.09f, A = 1.0f };
                var clearPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ClearValue>());
                Marshal.StructureToPtr(clear, clearPointer, false);
                long created;
                try
                {
                    created = IidResource.With(iid =>
                        Slot<CreateCommittedResourceFn>(_device, 27)(_device, ref heap, 0, ref texture, StateCommon, clearPointer, iid, out _texture));
                }
                finally
                {
                    Marshal.FreeHGlobal(clearPointer);
                }

                if (created < 0)
                {
                    throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a render target could not be created");
                }

                Slot<CreateRenderTargetViewFn>(_device, 20)(_device, _texture, IntPtr.Zero, _rtv);

                // Ask the device for the layout rather than guessing the pitch.
                Slot<GetCopyableFootprintsFn>(_device, 38)(
                    _device, ref texture, 0, 1, 0, out var layout, out _, out _, out _totalBytes);
                _rowPitch = layout.Footprint.RowPitch;
                if (_rowPitch == 0)
                {
                    _rowPitch = ((ulong)Width * 4 + 255) & ~255ul;
                    _totalBytes = _rowPitch * (ulong)Height;
                }

                var buffer = new ResourceDesc
                {
                    Dimension = DimensionBuffer,
                    Width = _totalBytes,
                    Height = 1,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    SampleDesc = new SampleDesc { Count = 1 },
                    Layout = 1,
                };

                var readbackHeap = new HeapProperties { Type = HeapTypeReadback };
                if (IidResource.With(iid =>
                    Slot<CreateCommittedResourceFn>(_device, 27)(_device, ref readbackHeap, 0, ref buffer, StateCopyDest, IntPtr.Zero, iid, out _readback)) < 0)
                {
                    throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a readback buffer could not be created");
                }

                if (IidFence.With(iid =>
                    Slot<CreateFenceFn>(_device, 36)(_device, 0, 0, iid, out _fence)) < 0)
                {
                    throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a fence could not be created");
                }
            }

            // ------------------------------------------------------------ the pipeline

            private IntPtr CompileStage(VM vm, string source, string target, string which)
            {
                var compile = Driver.Load()?.Compiler();
                if (compile is null)
                {
                    throw vm.Error("D3D12Error",
                        "the shader compiler is not available: d3dcompiler_47 could not be loaded");
                }

                var text = Marshal.StringToHGlobalAnsi(source);
                var name = Marshal.StringToHGlobalAnsi("tern.hlsl");
                var entry = Marshal.StringToHGlobalAnsi("main");
                var profile = Marshal.StringToHGlobalAnsi(target);
                IntPtr code = IntPtr.Zero;
                IntPtr errors = IntPtr.Zero;
                try
                {
                    var hr = compile(text, (IntPtr)source.Length, name, IntPtr.Zero, IntPtr.Zero,
                        entry, profile, 0, 0, out code, out errors);
                    if (hr < 0 || code == IntPtr.Zero)
                    {
                        var message = BlobText(errors);
                        Release(errors);
                        throw vm.Error("D3D12Error", message.Length == 0
                            ? $"the {which} shader did not compile (0x{(uint)hr:x8})"
                            : $"the {which} shader did not compile: {message}");
                    }

                    Release(errors);
                    return code;
                }
                finally
                {
                    Marshal.FreeHGlobal(text);
                    Marshal.FreeHGlobal(name);
                    Marshal.FreeHGlobal(entry);
                    Marshal.FreeHGlobal(profile);
                }
            }

            private static string BlobText(IntPtr blob)
            {
                if (blob == IntPtr.Zero)
                {
                    return string.Empty;
                }

                var size = (int)Slot<GetBufferSizeFn>(blob, 4)(blob);
                var bytes = Slot<GetBufferPointerFn>(blob, 3)(blob);
                if (size <= 0 || bytes == IntPtr.Zero)
                {
                    return string.Empty;
                }

                var text = Marshal.PtrToStringAnsi(bytes, size) ?? string.Empty;
                // The first line is the useful part; the rest is the caret diagram.
                var line = text.Split('\n')[0].Trim();
                return line;
            }

            private IntPtr RootSignature(VM vm)
            {
                var serialize = Driver.Load()?.SerializeRootSignature;
                if (serialize is null)
                {
                    throw vm.Error("D3D12Error",
                        "Direct3D 12 is not available on this system: D3D12SerializeRootSignature is missing");
                }

                // An empty root signature: the vertex data comes from a vertex
                // buffer, and the shaders take no constants.
                var description = new RootSignatureDesc { Flags = RootSignatureFlagAllowInputAssembler };
                var hr = serialize(ref description, RootSignatureVersion1, out var blob, out var errors);
                Release(errors);
                if (hr < 0 || blob == IntPtr.Zero)
                {
                    throw vm.Error("D3D12Error", "the root signature could not be serialized");
                }

                try
                {
                    var length = (IntPtr)(int)Slot<GetBufferSizeFn>(blob, 4)(blob);
                    var bytes = Slot<GetBufferPointerFn>(blob, 3)(blob);
                    var signature = IntPtr.Zero;
                    hr = IidRootSignature.With(iid =>
                        Slot<CreateRootSignatureFn>(_device, 16)(_device, 0, bytes, length, iid, out signature));
                    if (hr < 0 || signature == IntPtr.Zero)
                    {
                        throw vm.Error("D3D12Error", $"the root signature could not be created (0x{(uint)hr:x8})");
                    }

                    return signature;
                }
                finally
                {
                    Release(blob);
                }
            }

            public int MakeProgram(VM vm, string vertexSource, string pixelSource)
            {
                var vertex = CompileStage(vm, vertexSource, "vs_5_0", "vertex");
                IntPtr pixel = IntPtr.Zero;
                IntPtr signature = IntPtr.Zero;
                try
                {
                    pixel = CompileStage(vm, pixelSource, "ps_5_0", "pixel");
                    signature = RootSignature(vm);

                    var elements = new[]
                    {
                        new InputElement
                        {
                            SemanticName = "POSITION", Format = FormatR32G32B32Float,
                            InputSlot = 0, AlignedByteOffset = 0,
                        },
                        new InputElement
                        {
                            SemanticName = "COLOR", Format = FormatR32G32B32A32Float,
                            InputSlot = 0, AlignedByteOffset = 12,
                        },
                    };
                    var elementSize = Marshal.SizeOf<InputElement>();
                    var elementPointer = Marshal.AllocHGlobal(elementSize * elements.Length);
                    try
                    {
                        for (var i = 0; i < elements.Length; i++)
                        {
                            Marshal.StructureToPtr(elements[i], IntPtr.Add(elementPointer, i * elementSize), false);
                        }

                        var description = new GraphicsPipelineStateDesc
                        {
                            RootSignature = signature,
                            Vs = Bytecode(vertex),
                            Ps = Bytecode(pixel),
                            BlendState = OpaqueBlend(),
                            SampleMask = 0xFFFFFFFF,
                            RasterizerState = new RasterizerDesc
                            {
                                FillMode = FillModeSolid,
                                CullMode = CullModeNone,
                                DepthClipEnable = 1,
                            },
                            DepthStencilState = AlwaysDepth(),
                            InputLayout = new InputLayoutDesc { Elements = elementPointer, Count = 2 },
                            PrimitiveTopologyType = TopologyTypeTriangle,
                            NumRenderTargets = 1,
                            RtvFormats = new[] { FormatRgba8, 0, 0, 0, 0, 0, 0, 0 },
                            DsvFormat = FormatUnknown,
                            SampleDesc = new SampleDesc { Count = 1 },
                        };

                        var pipeline = IntPtr.Zero;
                        var hr = IidPipelineState.With(iid =>
                            Slot<CreateGraphicsPipelineStateFn>(_device, 10)(_device, ref description, iid, out pipeline));
                        if (hr < 0 || pipeline == IntPtr.Zero)
                        {
                            throw vm.Error("D3D12Error", $"the pipeline state could not be created (0x{(uint)hr:x8})");
                        }

                        _pipelines.Add(pipeline);
                        _signatures.Add(signature);
                        signature = IntPtr.Zero;   // owned by the target now
                        return _pipelines.Count;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(elementPointer);
                    }
                }
                finally
                {
                    Release(vertex);
                    Release(pixel);
                    Release(signature);
                }
            }

            private static ShaderBytecode Bytecode(IntPtr blob) => new ShaderBytecode
            {
                Bytecode = Slot<GetBufferPointerFn>(blob, 3)(blob),
                Length = (IntPtr)(int)Slot<GetBufferSizeFn>(blob, 4)(blob),
            };

            private static BlendDesc OpaqueBlend()
            {
                var targets = new RenderTargetBlend[8];
                for (var i = 0; i < targets.Length; i++)
                {
                    targets[i] = new RenderTargetBlend
                    {
                        SrcBlend = BlendOne,
                        DestBlend = 1,          // D3D12_BLEND_ZERO
                        BlendOp = BlendOpAdd,
                        SrcBlendAlpha = BlendOne,
                        DestBlendAlpha = 1,
                        BlendOpAlpha = BlendOpAdd,
                        LogicOp = LogicOpNoop,
                        WriteMask = 0xF,
                        Padding = new byte[3],
                    };
                }

                return new BlendDesc { Targets = targets };
            }

            private static DepthStencilDesc AlwaysDepth() => new DepthStencilDesc
            {
                DepthFunc = ComparisonAlways,
                FrontFace = new StencilOp { StencilFunc = ComparisonAlways },
                BackFace = new StencilOp { StencilFunc = ComparisonAlways },
                Padding = new byte[2],
            };

            public void Draw(VM vm, int program, float[] vertices)
            {
                var count = vertices.Length / 6;
                var packed = new float[count * VertexFloats];
                for (var i = 0; i < count; i++)
                {
                    packed[i * VertexFloats + 0] = vertices[i * 6 + 0];
                    packed[i * VertexFloats + 1] = vertices[i * 6 + 1];
                    packed[i * VertexFloats + 2] = 0.0f;
                    packed[i * VertexFloats + 3] = vertices[i * 6 + 2];
                    packed[i * VertexFloats + 4] = vertices[i * 6 + 3];
                    packed[i * VertexFloats + 5] = vertices[i * 6 + 4];
                    packed[i * VertexFloats + 6] = vertices[i * 6 + 5];
                }

                var view = Upload(vm, packed);
                Reset();
                Transition(StateRenderTarget);

                var handle = _rtv;
                Slot<OMSetRenderTargetsFn>(_list, 46)(_list, 1, ref handle, 0, IntPtr.Zero);

                // The state that decides whether anything is drawn at all.
                var viewport = new Viewport
                {
                    TopLeftX = 0, TopLeftY = 0, Width = Width, Height = Height, MinDepth = 0, MaxDepth = 1,
                };
                var scissor = new Rect { Left = 0, Top = 0, Right = Width, Bottom = Height };
                Slot<SetViewportsFn>(_list, 21)(_list, 1, ref viewport);
                Slot<SetScissorRectsFn>(_list, 22)(_list, 1, ref scissor);

                Slot<SetGraphicsRootSignatureFn>(_list, 30)(_list, _signatures[program - 1]);
                Slot<SetPipelineStateFn>(_list, 25)(_list, _pipelines[program - 1]);
                Slot<IASetPrimitiveTopologyFn>(_list, 20)(_list, TopologyTriangleList);
                Slot<SetVertexBuffersFn>(_list, 44)(_list, 0, 1, ref view);
                Slot<DrawInstancedFn>(_list, 12)(_list, (uint)count, 1, 0, 0);

                Transition(StateCopySource);
                Submit(vm, "d3d12.draw");
                CheckDevice(vm, "d3d12.draw");
            }

            /// <summary>
            /// A fresh upload-heap buffer per draw. Correct and simple; a renderer
            /// that draws thousands of times a frame would rotate them instead.
            /// </summary>
            private VertexBufferView Upload(VM vm, float[] vertices)
            {
                var bytes = (uint)Math.Max(1, vertices.Length * sizeof(float));
                var heap = new HeapProperties { Type = HeapTypeUpload };
                var buffer = new ResourceDesc
                {
                    Dimension = DimensionBuffer,
                    Width = bytes,
                    Height = 1,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    SampleDesc = new SampleDesc { Count = 1 },
                    Layout = 1,
                };

                var resource = IntPtr.Zero;
                var hr = IidResource.With(iid => Slot<CreateCommittedResourceFn>(_device, 27)(
                    _device, ref heap, 0, ref buffer, StateGenericRead, IntPtr.Zero, iid, out resource));
                if (hr < 0 || resource == IntPtr.Zero)
                {
                    throw vm.Error("D3D12Error", $"the vertex buffer could not be created (0x{(uint)hr:x8})");
                }

                var range = new Range { Begin = 0, End = 0 };
                hr = Slot<MapFn>(resource, 8)(resource, 0, ref range, out var data);
                if (hr < 0 || data == IntPtr.Zero)
                {
                    Release(resource);
                    throw RemovedError(vm, _device, "the vertex buffer could not be mapped", hr);
                }

                Marshal.Copy(vertices, 0, data, vertices.Length);
                Slot<UnmapFn>(resource, 9)(resource, 0, ref range);

                Release(_vertexBuffer);
                _vertexBuffer = resource;
                return new VertexBufferView
                {
                    BufferLocation = (ulong)Slot<GetGpuAddressFn>(resource, 11)(resource).ToInt64(),
                    SizeInBytes = bytes,
                    StrideInBytes = VertexFloats * sizeof(float),
                };
            }

            private void Reset()
            {
                Slot<ResetAllocatorFn>(_allocator, 8)(_allocator);
                Slot<ResetListFn>(_list, 10)(_list, _allocator, IntPtr.Zero);
            }

            /// <summary>
            /// Moves the render target to <paramref name="after"/>, naming the
            /// state it is really in. The calls used to write both states by
            /// hand, and <c>Clear</c> leaves the target in CopySource while
            /// <c>Draw</c> announced Common — a lie to the driver on every
            /// frame, which on real hardware can remove the device.
            /// </summary>
            private void Transition(uint after)
            {
                if (_state == after)
                {
                    return;
                }

                Barrier(_texture, _state, after);
                _state = after;
            }

            private void Barrier(IntPtr resource, uint before, uint after)
            {
                var barrier = new ResourceBarrier
                {
                    Type = 0,
                    Transition = new TransitionResource
                    {
                        Resource = resource,
                        Subresource = 0xFFFFFFFF,
                        StateBefore = before,
                        StateAfter = after,
                    },
                };
                Slot<ResourceBarrierFn>(_list, 26)(_list, 1, ref barrier);
            }

            // ----------------------------------------------------------------- the swap chain

            /// <summary>
            /// Copies the frame into the swap chain's next back buffer and
            /// presents it. Answers false when this machine cannot present that
            /// way, so the caller can fall back to the blit - which is slower,
            /// but is what a machine without IDXGIFactory2 has.
            /// </summary>
            public bool TryPresent(VM vm, IntPtr window)
            {
                if (_swapFailed)
                {
                    return false;
                }

                if (_swap != IntPtr.Zero && _swapWindow != window)
                {
                    DisposeSwap();
                }

                if (_swap == IntPtr.Zero && !OpenSwapChain(window))
                {
                    _swapFailed = true;
                    return false;
                }

                if (!ShowSwapChain(vm))
                {
                    DisposeSwap();
                    _swapFailed = true;
                    return false;
                }

                return true;
            }

            private bool OpenSwapChain(IntPtr window)
            {
                var driver = Driver.Load();
                if (driver is null)
                {
                    return false;
                }

                var factory = driver.CreateFactory();
                if (factory == IntPtr.Zero)
                {
                    return false;
                }

                var factory2 = IntPtr.Zero;
                var iid = IidFactory2.Allocate();
                try
                {
                    if (Slot<QueryInterfaceFn>(factory, 0)(factory, iid, out factory2) < 0)
                    {
                        factory2 = IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(iid);
                    Release(factory);
                }

                if (factory2 == IntPtr.Zero)
                {
                    return false;
                }

                var desc = new SwapChainDesc1
                {
                    Width = (uint)Width,
                    Height = (uint)Height,
                    Format = FormatRgba8,
                    Stereo = 0,
                    Sample = new SampleDesc { Count = 1, Quality = 0 },
                    BufferUsage = UsageRenderTargetOutput,
                    BufferCount = SwapChainBuffers,
                    Scaling = ScalingStretch,
                    SwapEffect = SwapEffectFlipDiscard,
                    AlphaMode = AlphaModeIgnore,
                    Flags = 0,
                };

                var chain = IntPtr.Zero;
                long hr;
                try
                {
                    hr = Slot<CreateSwapChainForHwndFn>(factory2, 15)(
                        factory2, _queue, window, ref desc, IntPtr.Zero, IntPtr.Zero, out chain);
                }
                finally
                {
                    Release(factory2);
                }

                if (hr < 0 || chain == IntPtr.Zero)
                {
                    return false;
                }

                // The back buffers are taken before the chain is adopted, so a
                // failure here leaves nothing half-open for the next frame.
                var buffers = new IntPtr[SwapChainBuffers];
                var resourceIid = IidResource.Allocate();
                try
                {
                    for (var i = 0; i < SwapChainBuffers; i++)
                    {
                        if (Slot<GetBufferFn>(chain, 9)(chain, (uint)i, resourceIid, out buffers[i]) < 0 ||
                            buffers[i] == IntPtr.Zero)
                        {
                            foreach (var taken in buffers)
                            {
                                Release(taken);
                            }

                            Release(chain);
                            return false;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(resourceIid);
                }

                for (var i = 0; i < SwapChainBuffers; i++)
                {
                    _swapBuffers[i] = buffers[i];
                    // A back buffer is created in the present state and must go
                    // back to it before Present. Tracking it here is what stops
                    // a barrier naming a state the buffer is not in.
                    _swapStates[i] = StatePresent;
                }

                _swap = chain;
                _swapWindow = window;
                _swapFrame = 0;
                return true;
            }

            private bool ShowSwapChain(VM vm)
            {
                var frame = _swapFrame;
                var buffer = _swapBuffers[frame];

                Reset();
                Transition(StateCopySource);
                Barrier(buffer, _swapStates[frame], StateCopyDest);
                _swapStates[frame] = StateCopyDest;

                var destination = new TextureCopyLocation
                {
                    Resource = buffer,
                    Type = CopyTypeSubresource,
                    SubresourceIndex = 0,
                };
                var source = new TextureCopyLocation
                {
                    Resource = _texture,
                    Type = CopyTypeSubresource,
                    SubresourceIndex = 0,
                };

                Slot<CopyTextureRegionFn>(_list, 16)(
                    _list, ref destination, 0, 0, 0, ref source, IntPtr.Zero);

                Barrier(buffer, StateCopyDest, StatePresent);
                _swapStates[frame] = StatePresent;
                Submit(vm, "d3d12.present");

                if (Slot<PresentFn>(_swap, 8)(_swap, 1, 0) < 0)
                {
                    return false;
                }

                _swapFrame = (frame + 1) % SwapChainBuffers;
                return true;
            }

            private void DisposeSwap()
            {
                for (var i = 0; i < SwapChainBuffers; i++)
                {
                    Release(_swapBuffers[i]);
                    _swapBuffers[i] = IntPtr.Zero;
                    _swapStates[i] = StatePresent;
                }

                Release(_swap);
                _swap = IntPtr.Zero;
                _swapWindow = IntPtr.Zero;
                _swapFrame = 0;
            }

            private void Submit(VM vm, string what)
            {
                Slot<CloseListFn>(_list, 9)(_list);

                var lists = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(lists, _list);
                    Slot<ExecuteCommandListsFn>(_queue, 10)(_queue, 1, lists);
                }
                finally
                {
                    Marshal.FreeHGlobal(lists);
                }

                _fenceValue++;
                Slot<FenceSignalFn>(_queue, 14)(_queue, _fence, _fenceValue);

                var completed = Slot<FenceValueFn>(_fence, 8);
                for (var waited = 0; waited < WaitTimeout; waited++)
                {
                    if (completed(_fence) >= _fenceValue)
                    {
                        // Read the driver's messages once the work has run, so a
                        // problem it reports at execution time is included.
                        ReportStoredMessages();
                        return;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                ReportStoredMessages();
                var code = (uint)Slot<DeviceRemovedReasonFn>(_device, 37)(_device);
                throw code != 0
                    ? vm.Error("D3D12Error",
                        $"the GPU did not finish within {WaitTimeout} ms during {what}: {RemovalText(code)} (0x{code:x8})")
                    : vm.Error("D3D12Error", $"the GPU did not finish within {WaitTimeout} ms during {what}");
            }

            /// <summary>
            /// Prints whatever the debug layer stored for this submit. Validation
            /// problems do not remove the device - that is the point of the layer -
            /// so this has to run on every submit, not only on a failure.
            /// </summary>
            private void ReportStoredMessages()
            {
                ReportMessages(_infoQueue);
            }

            /// <summary>
            /// DXGI_ERROR_DEVICE_REMOVED is reported at whatever call next
            /// touches the device, rarely the one that removed it. Asking after
            /// every submit turns "somewhere before this" into "during this".
            /// </summary>
            private void CheckDevice(VM vm, string what)
            {
                var code = (uint)Slot<DeviceRemovedReasonFn>(_device, 37)(_device);
                if (code != 0)
                {
                    ReportMessages(_infoQueue);
                    throw vm.Error("D3D12Error",
                        $"the device was removed during {what}: {RemovalText(code)} (0x{code:x8})");
                }
            }

            public void Clear(VM vm, float r, float g, float b, float a)
            {
                Reset();
                Transition(StateRenderTarget);

                var handle = _rtv;
                Slot<OMSetRenderTargetsFn>(_list, 46)(_list, 1, ref handle, 0, IntPtr.Zero);

                var colour = new[] { r, g, b, a };
                Slot<ClearRenderTargetViewFn>(_list, 48)(_list, _rtv, ref colour[0], 0, IntPtr.Zero);

                Transition(StateCopySource);
                Submit(vm, "d3d12.clear");
                CheckDevice(vm, "d3d12.clear");
            }

            private IntPtr CopyOut(VM vm, string what)
            {
                Reset();

                // Reading a texture requires it to be in COPY_SOURCE, and a second
                // read in a row has to be able to say so itself rather than trust
                // the last call to have left it there. Transitioning back to COMMON
                // afterwards was the habit; it also forces the driver to give up
                // the render-target layout, so it is slower as well as wrong.
                Transition(StateCopySource);

                var destination = new TextureCopyLocation
                {
                    Resource = _readback,
                    Type = CopyTypePlacedFootprint,
                    PlacedFootprint = new PlacedFootprint
                    {
                        Footprint = new SubresourceFootprint
                        {
                            Format = FormatRgba8,
                            Width = (uint)Width,
                            Height = (uint)Height,
                            Depth = 1,
                            RowPitch = (uint)_rowPitch,
                        },
                    },
                };
                var source = new TextureCopyLocation
                {
                    Resource = _texture,
                    Type = CopyTypeSubresource,
                    SubresourceIndex = 0,
                };

                Slot<CopyTextureRegionFn>(_list, 16)(_list, ref destination, 0, 0, 0, ref source, IntPtr.Zero);
                Submit(vm, what);
                CheckDevice(vm, what);

                var range = new Range { Begin = 0, End = 0 };
                var hr = Slot<MapFn>(_readback, 8)(_readback, 0, ref range, out var data);
                if (hr < 0 || data == IntPtr.Zero)
                {
                    throw RemovedError(vm, _device, "the readback buffer could not be mapped", hr);
                }

                return data;
            }

            public byte[] ReadPixels(VM vm, string what = "d3d12.pixels")
            {
                var data = CopyOut(vm, what);
                var pixels = new byte[Width * (long)Height * 4];
                var rowBytes = Width * 4;
                // Bottom row first, like gl.pixels, so a script does not care which
                // backend drew the picture.
                for (var y = Height - 1; y >= 0; y--)
                {
                    var row = IntPtr.Add(data, (int)((long)y * (long)_rowPitch));
                    Marshal.Copy(row, pixels, (Height - 1 - y) * rowBytes, rowBytes);
                }

                var range = new Range { Begin = 0, End = 0 };
                Slot<UnmapFn>(_readback, 9)(_readback, 0, ref range);
                return pixels;
            }

            public void Dispose()
            {
                // The swap chain holds back buffers, so it goes before the
                // device it was made on.
                DisposeSwap();

                foreach (var pipeline in _pipelines)
                {
                    Release(pipeline);
                }

                foreach (var signature in _signatures)
                {
                    Release(signature);
                }

                _pipelines.Clear();
                _signatures.Clear();
                Release(_vertexBuffer);
                _vertexBuffer = IntPtr.Zero;
                Release(_fence);
                Release(_readback);
                Release(_texture);
                Release(_heap);
                Release(_list);
                Release(_allocator);
                Release(_queue);
                Release(_device);
                _device = _queue = _allocator = _list = _heap = _texture = _readback = _fence = IntPtr.Zero;
                _infoQueue = IntPtr.Zero;
            }
        }

        private sealed class Targets
        {
            private readonly Dictionary<int, Target> _targets = new();
            private int _next = 1;

            public int Add(VM vm, Target target)
            {
                if (_targets.Count >= MaxDevices)
                {
                    target.Dispose();
                    throw vm.Error("D3D12Error", $"too many Direct3D 12 devices ({MaxDevices})");
                }

                var handle = _next++;
                _targets[handle] = target;
                return handle;
            }

            public Target Get(VM vm, in Value value)
            {
                if (value.Kind != ValueKind.Int)
                {
                    throw vm.Error("TypeError", $"expected a device handle, not {vm.TypeName(value)}");
                }

                var handle = (int)value.AsInt();
                if (_targets.TryGetValue(handle, out var target))
                {
                    return target;
                }

                throw vm.Error("D3D12Error", $"no open device with handle {handle}");
            }

            public void Remove(VM vm, in Value value)
            {
                var target = Get(vm, value);
                _targets.Remove((int)value.AsInt());
                target.Dispose();
            }
        }
    }
}
