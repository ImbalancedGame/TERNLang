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
    /// <c>gl</c>: a binding to real OpenGL, loaded at run time with
    /// <see cref="NativeLibrary"/> the way the native backend loads OpenSSL with
    /// <c>dlopen</c>. Nothing is linked, so a program that never renders does not
    /// depend on a driver being installed; the first <c>gl.create</c> asks libEGL
    /// for a surfaceless context with a pbuffer, which works on a headless box
    /// with a software driver (Mesa's llvmpipe, for instance).
    ///
    /// The API is deliberately small and shader-shaped: a context, a program made
    /// of a vertex and a fragment shader, a vertex buffer of <c>x, y, r, g, b, a</c>
    /// floats, and the pixels back. That is enough to be useful and small enough
    /// to keep the two back ends in step.
    /// </summary>
    internal static class GlModule
    {
        private const int MaxContexts = 8;
        private const int MaxPrograms = 64;
        private const int VertexFloats = 6;

        // EGL
        private const int EglNone = 0x3038;
        private const int EglSurfaceType = 0x3033;
        private const int EglPbufferBit = 0x0001;
        private const int EglRenderableType = 0x3040;
        private const int EglOpenGLBit = 0x0008;
        private const int EglRedSize = 0x3024;
        private const int EglGreenSize = 0x3023;
        private const int EglBlueSize = 0x3022;
        private const int EglAlphaSize = 0x3021;
        private const int EglDepthSize = 0x3025;
        private const int EglWidth = 0x3057;
        private const int EglHeight = 0x3056;
        private const int EglContextMajor = 0x3098;
        private const int EglContextMinor = 0x30FB;
        private const int EglContextProfileMask = 0x30FD;
        private const int EglOpenGLCoreProfileBit = 0x0001;
        private const int EglOpenGLAPI = 0x30A2;
        private const int EglPlatformSurfaceless = 0x313D;

        // GL
        private const uint GlColorBufferBit = 0x00004000;
        private const uint GlDepthBufferBit = 0x00000100;
        private const uint GlArrayBuffer = 0x8892;
        private const uint GlStaticDraw = 0x88E4;
        private const uint GlFloat = 0x1406;
        private const uint GlTriangles = 0x0004;
        private const uint GlVertexShader = 0x8B31;
        private const uint GlFragmentShader = 0x8B30;
        private const uint GlCompileStatus = 0x8B81;
        private const uint GlLinkStatus = 0x8B82;
        private const uint GlInfoLogLength = 0x8B84;
        private const uint GlRgba = 0x1908;
        private const uint GlUnsignedByte = 0x1401;
        private const uint GlVendor = 0x1F00;
        private const uint GlRenderer = 0x1F01;
        private const uint GlVersion = 0x1F02;
        private const uint GlShadingLanguageVersion = 0x8B8C;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();
            var contexts = new Contexts();

            Add(map, "available", 0, 0, (vm, a) =>
            {
                var driver = Driver.Load();
                return Value.Bool(driver is not null && driver.HasEgl);
            }, "True when libEGL could be loaded, so a context can be asked for.");

            Add(map, "info", 0, 0, (vm, a) =>
            {
                var context = contexts.Create(vm, 16, 16, "info");
                var report = new TernMap();
                report.Set(Value.Str("vendor"), Value.Str(context.Describe(GlVendor)));
                report.Set(Value.Str("renderer"), Value.Str(context.Describe(GlRenderer)));
                report.Set(Value.Str("version"), Value.Str(context.Describe(GlVersion)));
                report.Set(Value.Str("glsl"), Value.Str(context.Describe(GlShadingLanguageVersion)));
                contexts.Close(context);
                return Value.Of(report);
            }, "Describes the driver: {vendor, renderer, version, glsl}.");

            Add(map, "create", 2, 2, (vm, a) =>
            {
                var width = Dimension(vm, a[0], "width", 0);
                var height = Dimension(vm, a[1], "height", 1);
                return new Value(contexts.Add(vm, contexts.Create(vm, width, height, "create")));
            }, "Creates an offscreen context of the given size and returns a handle.");

            Add(map, "clear", 5, 5, (vm, a) =>
            {
                var context = contexts.Get(vm, a[0]);
                context.Clear((float)Unit(vm, a[1], "red"), (float)Unit(vm, a[2], "green"),
                    (float)Unit(vm, a[3], "blue"), (float)Unit(vm, a[4], "alpha"));
                return Value.Nil;
            }, "Fills the whole surface: gl.clear(handle, r, g, b, a), each 0..1.");

            Add(map, "program", 3, 3, (vm, a) =>
            {
                var context = contexts.Get(vm, a[0]);
                var vertex = NetModule.Text(vm, a[1], "vertex shader");
                var fragment = NetModule.Text(vm, a[2], "fragment shader");
                return new Value(context.Program(vm, vertex, fragment));
            }, "Compiles and links a shader program, returning a program handle.");

            Add(map, "draw", 3, 3, (vm, a) =>
            {
                var context = contexts.Get(vm, a[0]);
                var program = contexts.GetProgram(vm, a[1], context);
                var vertices = Vertices(vm, a[2]);
                context.Draw(vm, program, vertices);
                return Value.Nil;
            }, "Draws triangles: vertices are x, y, r, g, b, a floats, three per triangle.");

            Add(map, "pixels", 1, 1, (vm, a) =>
            {
                var context = contexts.Get(vm, a[0]);
                var bytes = context.Pixels(vm);
                var list = new TernList(bytes.Length);
                foreach (var b in bytes)
                {
                    list.Add(new Value(b));
                }

                return Value.Of(list);
            }, "Reads the surface back as RGBA bytes, bottom row first, like glReadPixels.");

            Add(map, "save", 2, 2, (vm, a) =>
            {
                var context = contexts.Get(vm, a[0]);
                var path = OutputPath(vm, a[1]);
                var bytes = context.Pixels(vm);
                try
                {
                    Png.Write(path, context.Width, context.Height, bytes, flip: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                  or NotSupportedException or ArgumentException)
                {
                    throw vm.Error("IOError", $"cannot write '{path}': {error.Message}");
                }

                return Value.Nil;
            }, "Saves the surface as a PNG: gl.save(handle, path).");

            Add(map, "size", 1, 1, (vm, a) =>
            {
                var context = contexts.Get(vm, a[0]);
                var list = new TernList(2);
                list.Add(new Value(context.Width));
                list.Add(new Value(context.Height));
                return Value.Of(list);
            }, "The [width, height] of a context.");

            Add(map, "close", 1, 1, (vm, a) =>
            {
                contexts.Remove(vm, a[0]);
                return Value.Nil;
            }, "Destroys a context and its programs.");

            engine.RegisterValue("gl", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:gl",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        private delegate void ClearColorFn(float r, float g, float b, float a);
        private delegate void ClearFn(uint mask);
        private delegate void ViewportFn(int x, int y, int width, int height);
        private delegate uint CreateShaderFn(uint type);
        private delegate void ShaderSourceFn(uint shader, int count, IntPtr strings, IntPtr lengths);
        private delegate void CompileShaderFn(uint shader);
        private delegate void GetShaderivFn(uint shader, uint name, out int value);
        private delegate void GetShaderInfoLogFn(uint shader, int maxLength, out int length, IntPtr info);
        private delegate uint CreateProgramFn();
        private delegate void AttachShaderFn(uint program, uint shader);
        private delegate void LinkProgramFn(uint program);
        private delegate void GetProgramivFn(uint program, uint name, out int value);
        private delegate void GetProgramInfoLogFn(uint program, int maxLength, out int length, IntPtr info);
        private delegate void UseProgramFn(uint program);
        private delegate void GenVertexArraysFn(int count, out uint arrays);
        private delegate void BindVertexArrayFn(uint array);
        private delegate void GenBuffersFn(int count, out uint buffers);
        private delegate void BindBufferFn(uint target, uint buffer);
        private delegate void BufferDataFn(uint target, IntPtr size, IntPtr data, uint usage);
        private delegate void EnableVertexAttribArrayFn(uint index);
        private delegate void VertexAttribPointerFn(uint index, int size, uint type, byte normalized, int stride, IntPtr pointer);
        private delegate void DrawArraysFn(uint mode, int first, int count);
        private delegate void ReadPixelsFn(int x, int y, int width, int height, uint format, uint type, IntPtr pixels);
        private delegate IntPtr GetStringFn(uint name);
        private delegate void FinishFn();
        private delegate void DeleteProgramFn(uint program);
        private delegate void DeleteShaderFn(uint shader);
        private delegate void DeleteBuffersFn(int count, ref uint buffer);
        private delegate void DeleteVertexArraysFn(int count, ref uint array);

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string documentation) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        // ------------------------------------------------------------------ arguments

        private static int Dimension(VM vm, in Value value, string what, int index)
        {
            var size = BuiltinMethods.RequireInt(value, "create", index);
            if (size < 1 || size > 8192)
            {
                throw vm.Error("ArgumentError", $"{what} must be between 1 and 8192");
            }

            return (int)size;
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

        private static float[] Vertices(VM vm, in Value value)
        {
            if (value.Kind != ValueKind.List)
            {
                throw vm.Error("TypeError", $"expected a list of vertices, not {vm.TypeName(value)}");
            }

            var list = value.AsList();
            if (list.Count % VertexFloats != 0)
            {
                throw vm.Error("ArgumentError",
                    $"vertices must be groups of {VertexFloats} numbers (x, y, r, g, b, a), not {list.Count}");
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

        // ---------------------------------------------------------------------- driver

        /// <summary>
        /// The EGL entry points, resolved once. Without a driver every call fails
        /// with the same message, so a script can ask <c>gl.available()</c> first
        /// and fall back.
        /// </summary>
        private sealed class Driver
        {
            public delegate IntPtr GetDisplayFn(IntPtr display);
            public delegate IntPtr GetPlatformDisplayFn(uint platform, IntPtr native, IntPtr attributes);
            public delegate int InitializeFn(IntPtr display, out int major, out int minor);
            public delegate int BindApiFn(uint api);
            public delegate int ChooseConfigFn(IntPtr display, IntPtr attributes, out IntPtr configs, int size, out int count);
            public delegate IntPtr CreateContextFn(IntPtr display, IntPtr config, IntPtr share, IntPtr attributes);
            public delegate IntPtr CreatePbufferSurfaceFn(IntPtr display, IntPtr config, IntPtr attributes);
            public delegate int MakeCurrentFn(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
            public delegate IntPtr GetProcAddressFn(IntPtr name);

            private static Driver? _cached;
            private static bool _attempted;

            public IntPtr Library { get; private set; }
            public GetDisplayFn? GetDisplay { get; private set; }
            public GetPlatformDisplayFn? GetPlatformDisplay { get; private set; }
            public InitializeFn? Initialize { get; private set; }
            public BindApiFn? BindApi { get; private set; }
            public ChooseConfigFn? ChooseConfig { get; private set; }
            public CreateContextFn? CreateContext { get; private set; }
            public CreatePbufferSurfaceFn? CreatePbufferSurface { get; private set; }
            public MakeCurrentFn? MakeCurrent { get; private set; }
            public GetProcAddressFn? GetProcAddress { get; private set; }

            public bool HasEgl => Initialize is not null && ChooseConfig is not null
                                  && CreateContext is not null && CreatePbufferSurface is not null
                                  && MakeCurrent is not null && GetProcAddress is not null
                                  && (GetDisplay is not null || GetPlatformDisplay is not null);

            public static Driver? Load()
            {
                if (_attempted)
                {
                    return _cached;
                }

                _attempted = true;
                // TERN_GL_LIBRARY lets a test (or a machine with the driver
                // somewhere odd) name the library; it is not part of the API.
                var names = new List<string>();
                var configured = Environment.GetEnvironmentVariable("TERN_GL_LIBRARY");
                if (!string.IsNullOrEmpty(configured))
                {
                    // An explicit choice is the whole list, so a missing path is
                    // an error instead of a silent fallback to another driver.
                    names.Add(configured);
                }
                else
                {
                    names.Add("libEGL.so.1");
                    names.Add("libEGL.so");
                }

                foreach (var name in names)
                {
                    if (!NativeLibrary.TryLoad(name, out var library))
                    {
                        continue;
                    }

                    var driver = new Driver { Library = library };
                    driver.GetDisplay = driver.Bind<GetDisplayFn>("eglGetDisplay");
                    driver.GetPlatformDisplay = driver.Bind<GetPlatformDisplayFn>("eglGetPlatformDisplayEXT");
                    driver.Initialize = driver.Bind<InitializeFn>("eglInitialize");
                    driver.BindApi = driver.Bind<BindApiFn>("eglBindAPI");
                    driver.ChooseConfig = driver.Bind<ChooseConfigFn>("eglChooseConfig");
                    driver.CreateContext = driver.Bind<CreateContextFn>("eglCreateContext");
                    driver.CreatePbufferSurface = driver.Bind<CreatePbufferSurfaceFn>("eglCreatePbufferSurface");
                    driver.MakeCurrent = driver.Bind<MakeCurrentFn>("eglMakeCurrent");
                    driver.GetProcAddress = driver.Bind<GetProcAddressFn>("eglGetProcAddress");

                    if (driver.HasEgl)
                    {
                        _cached = driver;
                        return driver;
                    }
                }

                return null;
            }

            private T? Bind<T>(string name) where T : Delegate
            {
                return NativeLibrary.TryGetExport(Library, name, out var address)
                    ? Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T
                    : null;
            }
        }

        // --------------------------------------------------------------------- context

        private sealed class Context : IDisposable
        {
            private readonly Driver _driver;
            private readonly IntPtr _display;
            private readonly IntPtr _surface;
            private readonly IntPtr _context;
            private readonly Dictionary<int, uint> _programs = new();
            private int _nextProgram = 1;

            private ClearColorFn? _clearColor;
            private ClearFn? _clear;
            private ViewportFn? _viewport;
            private CreateShaderFn? _createShader;
            private ShaderSourceFn? _shaderSource;
            private CompileShaderFn? _compileShader;
            private GetShaderivFn? _getShaderiv;
            private GetShaderInfoLogFn? _getShaderInfoLog;
            private CreateProgramFn? _createProgram;
            private AttachShaderFn? _attachShader;
            private LinkProgramFn? _linkProgram;
            private GetProgramivFn? _getProgramiv;
            private GetProgramInfoLogFn? _getProgramInfoLog;
            private UseProgramFn? _useProgram;
            private GenVertexArraysFn? _genVertexArrays;
            private BindVertexArrayFn? _bindVertexArray;
            private GenBuffersFn? _genBuffers;
            private BindBufferFn? _bindBuffer;
            private BufferDataFn? _bufferData;
            private EnableVertexAttribArrayFn? _enableVertexAttribArray;
            private VertexAttribPointerFn? _vertexAttribPointer;
            private DrawArraysFn? _drawArrays;
            private ReadPixelsFn? _readPixels;
            private GetStringFn? _getString;
            private FinishFn? _finish;
            private DeleteProgramFn? _deleteProgram;
            private DeleteShaderFn? _deleteShader;
            private DeleteBuffersFn? _deleteBuffers;
            private DeleteVertexArraysFn? _deleteVertexArrays;

            private uint _buffer;
            private uint _array;

            private Context(Driver driver, IntPtr display, IntPtr surface, IntPtr context, int width, int height)
            {
                _driver = driver;
                _display = display;
                _surface = surface;
                _context = context;
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }

            public static Context Create(VM vm, int width, int height, string operation)
            {
                var driver = Driver.Load();
                if (driver is null || !driver.HasEgl)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: libEGL could not be loaded");
                }

                // A surfaceless platform needs no X server; the fallback is the
                // default display, which some drivers only provide under one.
                var display = IntPtr.Zero;
                if (driver.GetPlatformDisplay is not null)
                {
                    display = driver.GetPlatformDisplay(EglPlatformSurfaceless, IntPtr.Zero, IntPtr.Zero);
                }

                if (display == IntPtr.Zero && driver.GetDisplay is not null)
                {
                    display = driver.GetDisplay(IntPtr.Zero);
                }

                if (display == IntPtr.Zero)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: no EGL display");
                }

                if (driver.Initialize!(display, out _, out _) == 0)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: eglInitialize failed");
                }

                if (driver.BindApi is not null && driver.BindApi(EglOpenGLAPI) == 0)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: EGL has no OpenGL support");
                }

                var configAttributes = new[]
                {
                    EglSurfaceType, EglPbufferBit,
                    EglRenderableType, EglOpenGLBit,
                    EglRedSize, 8, EglGreenSize, 8, EglBlueSize, 8, EglAlphaSize, 8,
                    EglDepthSize, 24,
                    EglNone,
                };

                if (ChooseConfig(driver, display, configAttributes, out var config) < 1)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: no matching EGL config");
                }

                var contextAttributes = new[]
                {
                    EglContextMajor, 3, EglContextMinor, 3,
                    EglContextProfileMask, EglOpenGLCoreProfileBit,
                    EglNone,
                };

                var context = WithAttributes(contextAttributes, pointer => driver.CreateContext!(display, config, IntPtr.Zero, pointer));
                if (context == IntPtr.Zero)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: cannot create an OpenGL 3.3 context");
                }

                var surfaceAttributes = new[] { EglWidth, width, EglHeight, height, EglNone };
                var surface = WithAttributes(surfaceAttributes, pointer => driver.CreatePbufferSurface!(display, config, pointer));
                if (surface == IntPtr.Zero)
                {
                    throw vm.Error("GLError", $"OpenGL is not available on this system: cannot create a {width}x{height} surface");
                }

                if (driver.MakeCurrent!(display, surface, surface, context) == 0)
                {
                    throw vm.Error("GLError", "OpenGL is not available on this system: cannot make the context current");
                }

                var result = new Context(driver, display, surface, context, width, height);
                result.BindFunctions(vm, operation);
                result.Prepare(vm, operation);
                return result;
            }

            /// <summary>
            /// EGL takes attribute lists as pointers to int arrays, so each call
            /// gets a temporary copy that is freed as soon as it returns.
            /// </summary>
            private static IntPtr WithAttributes(int[] values, Func<IntPtr, IntPtr> call)
            {
                var pointer = Marshal.AllocHGlobal(values.Length * sizeof(int));
                try
                {
                    Marshal.Copy(values, 0, pointer, values.Length);
                    return call(pointer);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }

            private static int ChooseConfig(Driver driver, IntPtr display, int[] attributes, out IntPtr config)
            {
                var pointer = Marshal.AllocHGlobal(attributes.Length * sizeof(int));
                try
                {
                    Marshal.Copy(attributes, 0, pointer, attributes.Length);
                    return driver.ChooseConfig!(display, pointer, out config, 1, out var count) == 0 ? 0 : count;
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }

            private void BindFunctions(VM vm, string operation)
            {
                T? Take<T>(string name) where T : Delegate
                {
                    var text = Marshal.StringToHGlobalAnsi(name);
                    try
                    {
                        var address = _driver.GetProcAddress!(text);
                        return address == IntPtr.Zero
                            ? null
                            : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(text);
                    }
                }

                _clearColor = Take<ClearColorFn>("glClearColor");
                _clear = Take<ClearFn>("glClear");
                _viewport = Take<ViewportFn>("glViewport");
                _createShader = Take<CreateShaderFn>("glCreateShader");
                _shaderSource = Take<ShaderSourceFn>("glShaderSource");
                _compileShader = Take<CompileShaderFn>("glCompileShader");
                _getShaderiv = Take<GetShaderivFn>("glGetShaderiv");
                _getShaderInfoLog = Take<GetShaderInfoLogFn>("glGetShaderInfoLog");
                _createProgram = Take<CreateProgramFn>("glCreateProgram");
                _attachShader = Take<AttachShaderFn>("glAttachShader");
                _linkProgram = Take<LinkProgramFn>("glLinkProgram");
                _getProgramiv = Take<GetProgramivFn>("glGetProgramiv");
                _getProgramInfoLog = Take<GetProgramInfoLogFn>("glGetProgramInfoLog");
                _useProgram = Take<UseProgramFn>("glUseProgram");
                _genVertexArrays = Take<GenVertexArraysFn>("glGenVertexArrays");
                _bindVertexArray = Take<BindVertexArrayFn>("glBindVertexArray");
                _genBuffers = Take<GenBuffersFn>("glGenBuffers");
                _bindBuffer = Take<BindBufferFn>("glBindBuffer");
                _bufferData = Take<BufferDataFn>("glBufferData");
                _enableVertexAttribArray = Take<EnableVertexAttribArrayFn>("glEnableVertexAttribArray");
                _vertexAttribPointer = Take<VertexAttribPointerFn>("glVertexAttribPointer");
                _drawArrays = Take<DrawArraysFn>("glDrawArrays");
                _readPixels = Take<ReadPixelsFn>("glReadPixels");
                _getString = Take<GetStringFn>("glGetString");
                _finish = Take<FinishFn>("glFinish");
                _deleteProgram = Take<DeleteProgramFn>("glDeleteProgram");
                _deleteShader = Take<DeleteShaderFn>("glDeleteShader");
                _deleteBuffers = Take<DeleteBuffersFn>("glDeleteBuffers");
                _deleteVertexArrays = Take<DeleteVertexArraysFn>("glDeleteVertexArrays");

                if (_clear is null || _clearColor is null || _viewport is null || _createShader is null
                    || _shaderSource is null || _compileShader is null || _getShaderiv is null
                    || _createProgram is null || _attachShader is null || _linkProgram is null
                    || _getProgramiv is null || _useProgram is null || _genVertexArrays is null
                    || _bindVertexArray is null || _genBuffers is null || _bindBuffer is null
                    || _bufferData is null || _enableVertexAttribArray is null || _vertexAttribPointer is null
                    || _drawArrays is null || _readPixels is null || _finish is null)
                {
                    throw vm.Error("GLError", $"OpenGL is not available on this system: the driver is missing what {operation} needs");
                }
            }

            private void Prepare(VM vm, string operation)
            {
                _viewport!(0, 0, Width, Height);
                _genVertexArrays!(1, out _array);
                _bindVertexArray!(_array);
                _genBuffers!(1, out _buffer);
                _bindBuffer!(GlArrayBuffer, _buffer);
                if (_array == 0 || _buffer == 0)
                {
                    throw vm.Error("GLError", $"OpenGL is not available on this system: cannot prepare {operation}");
                }
            }

            public void Clear(float r, float g, float b, float a)
            {
                _clearColor!(r, g, b, a);
                _clear!(GlColorBufferBit | GlDepthBufferBit);
                _finish!();
            }

            public int Program(VM vm, string vertexSource, string fragmentSource)
            {
                var vertex = Compile(vm, GlVertexShader, vertexSource, "vertex");
                var fragment = Compile(vm, GlFragmentShader, fragmentSource, "fragment");

                var program = _createProgram!();
                _attachShader!(program, vertex);
                _attachShader!(program, fragment);
                _linkProgram!(program);
                _getProgramiv!(program, GlLinkStatus, out var linked);
                if (linked == 0)
                {
                    var log = _getProgramInfoLog is null ? string.Empty : InfoLog(_getProgramInfoLog, program);
                    throw vm.Error("GLError", log.Length == 0 ? "the shader program did not link" : $"the shader program did not link: {log}");
                }

                _deleteShader!(vertex);
                _deleteShader!(fragment);

                var handle = _nextProgram++;
                _programs[handle] = program;
                return handle;
            }

            private uint Compile(VM vm, uint kind, string source, string which)
            {
                var shader = _createShader!(kind);
                var text = Marshal.StringToHGlobalAnsi(source);
                var pointer = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(pointer, text);
                try
                {
                    _shaderSource!(shader, 1, pointer, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                    Marshal.FreeHGlobal(text);
                }

                _compileShader!(shader);
                _getShaderiv!(shader, GlCompileStatus, out var compiled);
                if (compiled == 0)
                {
                    var log = _getShaderInfoLog is null ? string.Empty : InfoLog(_getShaderInfoLog, shader);
                    _deleteShader!(shader);
                    throw vm.Error("GLError", log.Length == 0
                        ? $"the {which} shader did not compile"
                        : $"the {which} shader did not compile: {log}");
                }

                return shader;
            }

            private static string InfoLog(Delegate function, uint objectId)
            {
                // Both info-log calls take (object, maxLength, &length, buffer) and
                // are looked up separately, so they share this by their signatures.
                var buffer = new byte[1024];
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var pointer = handle.AddrOfPinnedObject();
                    if (function is GetShaderInfoLogFn shader)
                    {
                        shader(objectId, buffer.Length, out _, pointer);
                    }
                    else if (function is GetProgramInfoLogFn program)
                    {
                        program(objectId, buffer.Length, out _, pointer);
                    }

                    var length = Array.IndexOf(buffer, (byte)0);
                    if (length < 0)
                    {
                        length = buffer.Length;
                    }

                    return Encoding.ASCII.GetString(buffer, 0, length).Replace("\r", " ").Replace("\n", " ").Trim();
                }
                finally
                {
                    handle.Free();
                }
            }

            public void Draw(VM vm, uint program, float[] vertices)
            {
                _useProgram!(program);
                _bindVertexArray!(_array);
                _bindBuffer!(GlArrayBuffer, _buffer);

                var bytes = vertices.Length * sizeof(float);
                var pointer = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.Copy(vertices, 0, pointer, vertices.Length);
                    _bufferData!(GlArrayBuffer, bytes, pointer, GlStaticDraw);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }

                var stride = VertexFloats * sizeof(float);
                _enableVertexAttribArray!(0);
                _vertexAttribPointer!(0, 2, GlFloat, 0, stride, IntPtr.Zero);
                _enableVertexAttribArray!(1);
                _vertexAttribPointer!(1, 4, GlFloat, 0, stride, 2 * sizeof(float));
                _drawArrays!(GlTriangles, 0, vertices.Length / VertexFloats);
                _finish!();
            }

            public byte[] Pixels(VM vm)
            {
                var pixels = new byte[Width * Height * 4];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    _readPixels!(0, 0, Width, Height, GlRgba, GlUnsignedByte, handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }

                return pixels;
            }

            public string Describe(uint name) => _getString is null
                ? string.Empty
                : Marshal.PtrToStringAnsi(_getString(name)) ?? string.Empty;

            public uint ProgramId(VM vm, int handle)
            {
                if (!_programs.TryGetValue(handle, out var program))
                {
                    throw vm.Error("GLError", $"no shader program with handle {handle}");
                }

                return program;
            }

            public void Dispose()
            {
                foreach (var program in _programs.Values)
                {
                    _deleteProgram?.Invoke(program);
                }

                _programs.Clear();
                if (_buffer != 0)
                {
                    _deleteBuffers?.Invoke(1, ref _buffer);
                }

                if (_array != 0)
                {
                    _deleteVertexArrays?.Invoke(1, ref _array);
                }
            }
        }

        /// <summary>
        /// The contexts a script has open. One table per engine, like the file
        /// handles, so two engines cannot see each other's surfaces.
        /// </summary>
        private sealed class Contexts
        {
            private readonly Dictionary<int, Context> _contexts = new();
            private readonly Dictionary<int, Context> _programOwners = new();
            private int _next = 1;

            public Context Create(VM vm, int width, int height, string operation) =>
                Context.Create(vm, width, height, operation);

            public int Add(VM vm, Context context)
            {
                if (_contexts.Count >= MaxContexts)
                {
                    context.Dispose();
                    throw vm.Error("GLError", $"too many OpenGL contexts ({MaxContexts})");
                }

                var handle = _next++;
                _contexts[handle] = context;
                return handle;
            }

            public Context Get(VM vm, in Value value)
            {
                if (value.Kind != ValueKind.Int)
                {
                    throw vm.Error("TypeError", $"expected a context handle, not {vm.TypeName(value)}");
                }

                var handle = (int)value.AsInt();
                if (_contexts.TryGetValue(handle, out var context))
                {
                    return context;
                }

                throw vm.Error("GLError", $"no open context with handle {handle}");
            }

            public uint GetProgram(VM vm, in Value value, Context context)
            {
                if (value.Kind != ValueKind.Int)
                {
                    throw vm.Error("TypeError", $"expected a program handle, not {vm.TypeName(value)}");
                }

                return context.ProgramId(vm, (int)value.AsInt());
            }

            public void Remove(VM vm, in Value value)
            {
                var context = Get(vm, value);
                _contexts.Remove((int)value.AsInt());
                context.Dispose();
            }

            public void Close(Context context) => context.Dispose();
        }
    }

    /// <summary>
    /// A PNG writer with no compression: the deflate stream is made of stored
    /// blocks, which is valid, larger on disk, and needs nothing but a CRC table.
    /// </summary>
    internal static class Png
    {
        private static readonly uint[] Crc = BuildCrcTable();

        public static void Write(string path, int width, int height, byte[] rgba, bool flip)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;   // bits per sample
            header[9] = 6;   // RGBA
            header[10] = 0;
            header[11] = 0;
            header[12] = 0;
            WriteChunk(file, "IHDR", header);

            // Filter byte 0 in front of every row, rows written top to bottom.
            var raw = new byte[(width * 4 + 1) * height];
            for (var row = 0; row < height; row++)
            {
                var source = flip ? (height - 1 - row) : row;
                raw[row * (width * 4 + 1)] = 0;
                Buffer.BlockCopy(rgba, source * width * 4, raw, row * (width * 4 + 1) + 1, width * 4);
            }

            WriteChunk(file, "IDAT", Deflate(raw));
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        private static byte[] Deflate(byte[] data)
        {
            var output = new List<byte>(data.Length + 64) { 0x78, 0x01 };
            var offset = 0;
            do
            {
                var length = Math.Min(65535, data.Length - offset);
                var final = offset + length >= data.Length ? 1 : 0;
                output.Add((byte)final);
                output.Add((byte)(length & 0xFF));
                output.Add((byte)(length >> 8));
                output.Add((byte)(~length & 0xFF));
                output.Add((byte)((~length >> 8) & 0xFF));
                for (var i = 0; i < length; i++)
                {
                    output.Add(data[offset + i]);
                }

                offset += length;
            }
            while (offset < data.Length);

            var checksum = Adler32(data);
            output.Add((byte)(checksum >> 24));
            output.Add((byte)(checksum >> 16));
            output.Add((byte)(checksum >> 8));
            output.Add((byte)checksum);
            return output.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[8];
            WriteBigEndian(header, 0, (uint)data.Length);
            for (var i = 0; i < 4; i++)
            {
                header[4 + i] = (byte)type[i];
            }

            stream.Write(header);
            stream.Write(data);

            var crc = 0xFFFFFFFFu;
            for (var i = 4; i < 8; i++)
            {
                crc = Crc[(crc ^ header[i]) & 0xFF] ^ (crc >> 8);
            }

            foreach (var value in data)
            {
                crc = Crc[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            var trailer = new byte[4];
            WriteBigEndian(trailer, 0, crc ^ 0xFFFFFFFFu);
            stream.Write(trailer);
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
