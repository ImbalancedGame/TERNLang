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

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();
            var targets = new Targets();

            Add(map, "available", 0, 0, (vm, a) => Value.Bool(Driver.Load() is not null),
                "True when d3d12.dll could be loaded (or TERN_D3D12_LIBRARY points at one).");

            Add(map, "info", 0, 0, (vm, a) =>
            {
                var driver = Driver.Load() ?? throw vm.Error("D3D12Error", Unavailable);
                var report = new TernMap();
                report.Set(Value.Str("name"), Value.Str(driver.AdapterName()));
                report.Set(Value.Str("nodes"), new Value(1));
                return Value.Of(report);
            }, "Describes the adapter: {name, nodes}.");

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
                var bytes = target.ReadPixels(vm);
                try
                {
                    Png.Write(path, target.Width, target.Height, bytes, flip: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                  or NotSupportedException or ArgumentException)
                {
                    throw vm.Error("IOError", $"cannot write '{path}': {error.Message}");
                }

                return Value.Nil;
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
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(engine.WorkingDirectory, path));
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

        [StructLayout(LayoutKind.Explicit)]
        private struct TextureCopyLocation
        {
            [FieldOffset(0)] public IntPtr Resource;
            [FieldOffset(8)] public int Type;
            [FieldOffset(16)] public PlacedFootprint PlacedFootprint;
            [FieldOffset(16)] public uint SubresourceIndex;
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
        }

        private static readonly Guid IidDevice =
            Guid.Of(0x189819f1, 0x1b6b, 0x4ec5, 0xba, 0xaf, 0x95, 0xa3, 0x7f, 0x9e, 0x51, 0x55);

        private static readonly Guid IidFactory1 =
            Guid.Of(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);

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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyTextureRegionFn(IntPtr self, ref TextureCopyLocation destination, uint x, uint y, ref TextureCopyLocation source, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ExecuteCommandListsFn(IntPtr self, uint count, IntPtr lists);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long FenceSignalFn(IntPtr self, IntPtr fence, ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long MapFn(IntPtr self, uint subresource, ref Range range, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, uint subresource, ref Range range);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate ulong ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GetDesc1Fn(IntPtr self, IntPtr description);

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

            private delegate long CreateDeviceFn(IntPtr adapter, int level, IntPtr iid, out IntPtr device);
            private delegate long CreateFactoryFn(IntPtr iid, out IntPtr factory);

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

                    _cached = new Driver(library, createDevice, dxgi, createFactory);
                    return _cached;
                }

                return null;
            }

            public IntPtr CreateDevice(VM vm, int width, int height)
            {
                var iid = IidDevice.Allocate();
                try
                {
                    var hr = _createDevice(IntPtr.Zero, FeatureLevel11, iid, out var device);
                    if (hr < 0 || device == IntPtr.Zero)
                    {
                        throw vm.Error("D3D12Error",
                            $"Direct3D 12 is not available on this system: D3D12CreateDevice failed (0x{(uint)hr:X8})");
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
            private IntPtr _texture;
            private IntPtr _readback;
            private IntPtr _fence;
            private CpuDescriptorHandle _rtv;
            private ulong _fenceValue;
            private ulong _rowPitch;
            private ulong _totalBytes;

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
                target.Prepare(vm);
                return target;
            }

            private void Prepare(VM vm)
            {
                var iid = IidDevice.Allocate();
                try
                {
                    var queueDesc = new CommandQueueDesc { Type = CommandListTypeDirect };
                    var queueDescPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CommandQueueDesc>());
                    try
                    {
                        Marshal.StructureToPtr(queueDesc, queueDescPointer, false);
                        if (Slot<CreateCommandQueueFn>(_device, 8)(_device, queueDescPointer, iid, out _queue) < 0)
                        {
                            throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a command queue could not be created");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(queueDescPointer);
                    }

                    if (Slot<CreateCommandAllocatorFn>(_device, 9)(_device, CommandListTypeDirect, iid, out _allocator) < 0)
                    {
                        throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a command allocator could not be created");
                    }

                    if (Slot<CreateCommandListFn>(_device, 12)(_device, CommandListTypeDirect, 0, _allocator, IntPtr.Zero, iid, out _list) < 0)
                    {
                        throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a command list could not be created");
                    }

                    Slot<CloseListFn>(_list, 9)(_list);

                    var heapDesc = new DescriptorHeapDesc { Type = DescriptorHeapTypeRtv, NumDescriptors = 1 };
                    if (Slot<CreateDescriptorHeapFn>(_device, 14)(_device, ref heapDesc, iid, out _heap) < 0)
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
                    if (Slot<CreateCommittedResourceFn>(_device, 27)(_device, ref heap, 0, ref texture, StateCommon, IntPtr.Zero, iid, out _texture) < 0)
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
                    if (Slot<CreateCommittedResourceFn>(_device, 27)(_device, ref readbackHeap, 0, ref buffer, StateCopyDest, IntPtr.Zero, iid, out _readback) < 0)
                    {
                        throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a readback buffer could not be created");
                    }

                    if (Slot<CreateFenceFn>(_device, 36)(_device, 0, 0, iid, out _fence) < 0)
                    {
                        throw vm.Error("D3D12Error", "Direct3D 12 is not available on this system: a fence could not be created");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(iid);
                }
            }

            private void Reset()
            {
                Slot<ResetAllocatorFn>(_allocator, 8)(_allocator);
                Slot<ResetListFn>(_list, 10)(_list, _allocator, IntPtr.Zero);
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

            private void Submit(VM vm)
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
                        return;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                throw vm.Error("D3D12Error", $"the GPU did not finish within {WaitTimeout} ms");
            }

            public void Clear(VM vm, float r, float g, float b, float a)
            {
                Reset();
                Barrier(_texture, StateCommon, StateRenderTarget);

                var handle = _rtv;
                Slot<OMSetRenderTargetsFn>(_list, 46)(_list, 1, ref handle, 0, IntPtr.Zero);

                var colour = new[] { r, g, b, a };
                Slot<ClearRenderTargetViewFn>(_list, 48)(_list, _rtv, ref colour[0], 0, IntPtr.Zero);

                Barrier(_texture, StateRenderTarget, StateCopySource);
                Submit(vm);
            }

            private IntPtr CopyOut(VM vm)
            {
                Reset();

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

                Slot<CopyTextureRegionFn>(_list, 16)(_list, ref destination, 0, 0, ref source, IntPtr.Zero);
                Barrier(_texture, StateCopySource, StateCommon);
                Submit(vm);

                var range = new Range { Begin = 0, End = 0 };
                var hr = Slot<MapFn>(_readback, 8)(_readback, 0, ref range, out var data);
                if (hr < 0 || data == IntPtr.Zero)
                {
                    throw vm.Error("D3D12Error", $"the readback buffer could not be mapped (0x{(uint)hr:X8})");
                }

                return data;
            }

            public byte[] ReadPixels(VM vm)
            {
                var data = CopyOut(vm);
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
                Release(_fence);
                Release(_readback);
                Release(_texture);
                Release(_heap);
                Release(_list);
                Release(_allocator);
                Release(_queue);
                Release(_device);
                _device = _queue = _allocator = _list = _heap = _texture = _readback = _fence = IntPtr.Zero;
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
