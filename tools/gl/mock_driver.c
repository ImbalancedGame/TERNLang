// A fake libEGL + OpenGL for the tests.
//
// It exports the entry points the binding calls and behaves just enough like a
// driver to prove the whole path: a pbuffer of the requested size, a shader that
// fails to compile when its source contains "FAIL", and glDrawArrays that paints
// the first vertex's colour into the top-left pixel. That last trick is what
// shows the vertex data really travelled through glBufferData and
// glVertexAttribPointer, without needing a GPU.
//
// Build it and point TERN_GL_LIBRARY at it:
//     cc -shared -fPIC -o /tmp/mock/libEGL.so.1 tools/gl/mock_driver.c
//     TERN_GL_LIBRARY=/tmp/mock/libEGL.so.1 ./tools/tern tools/gl/probe.tern

#include <stdlib.h>
#include <string.h>

#define EGL_NONE 0x3038
#define EGL_WIDTH 0x3057
#define EGL_HEIGHT 0x3056

#define GL_COMPILE_STATUS 0x8B81
#define GL_MAX_OBJECTS 256

typedef struct {
    int status;
    char source[256];
} MockShader;

static MockShader mock_shaders[GL_MAX_OBJECTS];
#define GL_LINK_STATUS 0x8B82
#define GL_VENDOR 0x1F00
#define GL_RENDERER 0x1F01
#define GL_VERSION 0x1F02
#define GL_SHADING_LANGUAGE_VERSION 0x8B8C

typedef struct {
    int width, height;
    unsigned char *pixels;
} MockSurface;

static MockSurface *mock_current;
static float mock_clear[4] = { 0, 0, 0, 1 };
static float *mock_vertices;
static int mock_vertex_count;
static unsigned mock_next_object = 100;

static void mock_fill(MockSurface *surface) {
    if (!surface) return;
    for (int i = 0; i < surface->width * surface->height; i++) {
        unsigned char *pixel = surface->pixels + (size_t)i * 4;
        pixel[0] = (unsigned char)(mock_clear[0] * 255.0f + 0.5f);
        pixel[1] = (unsigned char)(mock_clear[1] * 255.0f + 0.5f);
        pixel[2] = (unsigned char)(mock_clear[2] * 255.0f + 0.5f);
        pixel[3] = (unsigned char)(mock_clear[3] * 255.0f + 0.5f);
    }
}

// ------------------------------------------------------------------- EGL

void *eglGetDisplay(void *display) { (void)display; return (void *)0x1; }
void *eglGetPlatformDisplayEXT(unsigned platform, void *native, const int *attributes) {
    (void)platform; (void)native; (void)attributes;
    return (void *)0x2;
}
int eglInitialize(void *display, int *major, int *minor) {
    (void)display;
    if (major) *major = 1;
    if (minor) *minor = 5;
    return 1;
}
int eglBindAPI(unsigned api) { (void)api; return 1; }
int eglChooseConfig(void *display, const int *attributes, void **configs, int size, int *count) {
    (void)display; (void)attributes;
    if (size < 1) return 0;
    configs[0] = (void *)0x10;
    *count = 1;
    return 1;
}
void *eglCreateContext(void *display, void *config, void *share, const int *attributes) {
    (void)display; (void)config; (void)share; (void)attributes;
    return (void *)0x20;
}
void *eglCreatePbufferSurface(void *display, void *config, const int *attributes) {
    (void)display; (void)config;
    int width = 0, height = 0;
    for (const int *at = attributes; at && *at != EGL_NONE; at += 2) {
        if (at[0] == EGL_WIDTH) width = at[1];
        if (at[0] == EGL_HEIGHT) height = at[1];
    }
    if (width <= 0 || height <= 0) return NULL;
    MockSurface *surface = (MockSurface *)calloc(1, sizeof(MockSurface));
    surface->width = width;
    surface->height = height;
    surface->pixels = (unsigned char *)calloc((size_t)width * (size_t)height * 4, 1);
    return surface;
}
int eglMakeCurrent(void *display, void *draw, void *read, void *context) {
    (void)display; (void)read; (void)context;
    mock_current = (MockSurface *)draw;
    return 1;
}
void eglDestroySurface(void *display, void *surface) {
    (void)display;
    MockSurface *target = (MockSurface *)surface;
    if (!target) return;
    if (mock_current == target) mock_current = NULL;
    free(target->pixels);
    free(target);
}
void eglDestroyContext(void *display, void *context) { (void)display; (void)context; }

// -------------------------------------------------------------------- GL

void glClearColor(float r, float g, float b, float a) {
    mock_clear[0] = r; mock_clear[1] = g; mock_clear[2] = b; mock_clear[3] = a;
}
void glClear(unsigned mask) { (void)mask; mock_fill(mock_current); }
void glViewport(int x, int y, int width, int height) { (void)x; (void)y; (void)width; (void)height; }
unsigned glCreateShader(unsigned type) {
    (void)type;
    unsigned shader = mock_next_object++;
    if (shader < GL_MAX_OBJECTS) {
        mock_shaders[shader].status = 1;
        mock_shaders[shader].source[0] = '\0';
    }
    return shader;
}
void glShaderSource(unsigned shader, int count, const char *const *strings, const int *lengths) {
    (void)count; (void)lengths;
    if (shader >= GL_MAX_OBJECTS || !strings) return;
    strncpy(mock_shaders[shader].source, strings[0], sizeof mock_shaders[shader].source - 1);
    mock_shaders[shader].source[sizeof mock_shaders[shader].source - 1] = '\0';
}
void glCompileShader(unsigned shader) {
    if (shader >= GL_MAX_OBJECTS) return;
    mock_shaders[shader].status = strstr(mock_shaders[shader].source, "FAIL") ? 0 : 1;
}
void glGetShaderiv(unsigned shader, unsigned name, int *value) {
    *value = 1;
    if (name == GL_COMPILE_STATUS && shader < GL_MAX_OBJECTS) *value = mock_shaders[shader].status;
}
void glGetShaderInfoLog(unsigned shader, int maxLength, int *length, char *info) {
    (void)shader;
    const char *text = "mock shader log";
    strncpy(info, text, (size_t)maxLength);
    if (length) *length = (int)strlen(text);
}
unsigned glCreateProgram(void) {
    unsigned program = mock_next_object++;
    if (program < GL_MAX_OBJECTS) mock_shaders[program].status = 1;
    return program;
}
void glAttachShader(unsigned program, unsigned shader) {
    if (program >= GL_MAX_OBJECTS || shader >= GL_MAX_OBJECTS) return;
    if (!mock_shaders[shader].status) mock_shaders[program].status = 0;
}
void glLinkProgram(unsigned program) {
    if (program >= GL_MAX_OBJECTS) return;
    if (!mock_shaders[program].status) strncpy(mock_shaders[program].source, "FAIL", 4);
}
void glGetProgramiv(unsigned program, unsigned name, int *value) {
    *value = 1;
    if (name == GL_LINK_STATUS && program < GL_MAX_OBJECTS) {
        *value = mock_shaders[program].status;
    }
}
void glGetProgramInfoLog(unsigned program, int maxLength, int *length, char *info) {
    (void)program;
    const char *text = "mock program log";
    strncpy(info, text, (size_t)maxLength);
    if (length) *length = (int)strlen(text);
}
void glUseProgram(unsigned program) { (void)program; }
void glGenVertexArrays(int count, unsigned *arrays) {
    for (int i = 0; i < count; i++) arrays[i] = mock_next_object++;
}
void glBindVertexArray(unsigned array) { (void)array; }
void glGenBuffers(int count, unsigned *buffers) {
    for (int i = 0; i < count; i++) buffers[i] = mock_next_object++;
}
void glBindBuffer(unsigned target, unsigned buffer) { (void)target; (void)buffer; }
void glBufferData(unsigned target, long size, const void *data, unsigned usage) {
    (void)target; (void)usage;
    free(mock_vertices);
    mock_vertex_count = (int)(size / (long)sizeof(float));
    mock_vertices = (float *)malloc((size_t)size);
    memcpy(mock_vertices, data, (size_t)size);
}
void glEnableVertexAttribArray(unsigned index) { (void)index; }
void glVertexAttribPointer(unsigned index, int size, unsigned type, unsigned char normalized,
                           int stride, const void *pointer) {
    (void)index; (void)size; (void)type; (void)normalized; (void)stride; (void)pointer;
}
void glDrawArrays(unsigned mode, int first, int count) {
    (void)mode; (void)first; (void)count;
    // Paint the first vertex's colour into the top-left pixel so a read-back
    // shows that the vertex buffer arrived.
    if (!mock_current || mock_vertex_count < 6) return;
    unsigned char *pixel = mock_current->pixels;
    pixel[0] = (unsigned char)(mock_vertices[2] * 255.0f + 0.5f);
    pixel[1] = (unsigned char)(mock_vertices[3] * 255.0f + 0.5f);
    pixel[2] = (unsigned char)(mock_vertices[4] * 255.0f + 0.5f);
    pixel[3] = (unsigned char)(mock_vertices[5] * 255.0f + 0.5f);
}
void glReadPixels(int x, int y, int width, int height, unsigned format, unsigned type, void *pixels) {
    (void)x; (void)y; (void)format; (void)type;
    if (!mock_current || width != mock_current->width || height != mock_current->height) return;
    memcpy(pixels, mock_current->pixels, (size_t)width * (size_t)height * 4);
}
const unsigned char *glGetString(unsigned name) {
    switch (name) {
        case GL_VENDOR: return (const unsigned char *)"TERN mock";
        case GL_RENDERER: return (const unsigned char *)"TERN mock driver";
        case GL_VERSION: return (const unsigned char *)"3.3 mock";
        case GL_SHADING_LANGUAGE_VERSION: return (const unsigned char *)"330 mock";
        default: return (const unsigned char *)"";
    }
}
void glFinish(void) {}
void glDeleteProgram(unsigned program) { (void)program; }
void glDeleteShader(unsigned shader) { (void)shader; }
void glDeleteBuffers(int count, const unsigned *buffers) { (void)count; (void)buffers; }
void glDeleteVertexArrays(int count, const unsigned *arrays) { (void)count; (void)arrays; }

void *eglGetProcAddress(const char *name) {
    if (!name) return NULL;
    #define PROC(fn) if (strcmp(name, #fn) == 0) return (void *)(fn)
    PROC(glClearColor); PROC(glClear); PROC(glViewport); PROC(glCreateShader);
    PROC(glShaderSource); PROC(glCompileShader); PROC(glGetShaderiv);
    PROC(glGetShaderInfoLog); PROC(glCreateProgram); PROC(glAttachShader);
    PROC(glLinkProgram); PROC(glGetProgramiv); PROC(glGetProgramInfoLog);
    PROC(glUseProgram); PROC(glGenVertexArrays); PROC(glBindVertexArray);
    PROC(glGenBuffers); PROC(glBindBuffer); PROC(glBufferData);
    PROC(glEnableVertexAttribArray); PROC(glVertexAttribPointer); PROC(glDrawArrays);
    PROC(glReadPixels); PROC(glGetString); PROC(glFinish); PROC(glDeleteProgram);
    PROC(glDeleteShader); PROC(glDeleteBuffers); PROC(glDeleteVertexArrays);
    #undef PROC
    return NULL;
}
