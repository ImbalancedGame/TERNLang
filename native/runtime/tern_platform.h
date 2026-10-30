// The small part of the runtime that has to know which system it is on.
//
// Everything in tern_rt.c is plain C; this header supplies the handful of names
// the two platforms spell differently, so the runtime itself stays readable and
// the differences live in one place. Windows builds are produced on any machine
// with native/ternc.py --target windows, which drives a mingw-w64 (Zig) cross
// compiler; see native/README.md.
#ifndef TERN_PLATFORM_H
#define TERN_PLATFORM_H

#ifdef _WIN32

// Large files: the user's files are allowed to be more than 2 GiB.
#define _FILE_OFFSET_BITS 64

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <windows.h>

#include <direct.h>
#include <errno.h>
#include <fcntl.h>
#include <io.h>
#include <locale.h>
#include <math.h>
#include <process.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>
#include <wchar.h>
#include <wctype.h>

// The CRT's names for the POSIX calls. Keeping the POSIX spelling means the
// runtime reads the same on both platforms.
#define mkdir(path, mode) _mkdir(path)
#define rmdir _rmdir
#define unlink _unlink
#define access _access
#define getcwd _getcwd
#define isatty _isatty
#define fileno _fileno
#define strdup _strdup
#define strcasecmp _stricmp
#define lstat stat
#define setenv(name, value, overwrite) _putenv_s((name), (value))

// Winsock has no SIGPIPE, so the send() flag that suppresses it is a no-op.
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif

// memmem is a GNU extension; the runtime leans on it for substring search, so
// Windows gets the same function under the same name.
static void *tern_memmem(const void *haystack, size_t haystack_length,
                         const void *needle, size_t needle_length) {
    if (needle_length == 0) {
        return (void *)haystack;
    }
    if (haystack_length < needle_length) {
        return NULL;
    }
    const char *start = (const char *)haystack;
    const char *last = start + haystack_length - needle_length;
    for (const char *at = start; at <= last; at++) {
        if (*at == *(const char *)needle && memcmp(at, needle, needle_length) == 0) {
            return (void *)at;
        }
    }
    return NULL;
}
#define memmem(haystack, haystack_length, needle, needle_length) \
    tern_memmem((haystack), (haystack_length), (needle), (needle_length))

// gmtime_r does not exist here; gmtime is fine because the runtime is single
// threaded and the result is copied immediately.
static struct tm *tern_gmtime_r(const time_t *clock, struct tm *result) {
    struct tm *value = gmtime(clock);
    if (value) {
        *result = *value;
        return result;
    }
    return NULL;
}
#define gmtime_r(clock, result) tern_gmtime_r((clock), (result))

// Directory iteration, so `fs.list` and `remove_tree` stay written against the
// POSIX shape they were written for.
struct dirent {
    char d_name[FILENAME_MAX];
};

typedef struct TernDir {
    HANDLE handle;
    WIN32_FIND_DATAA data;
    struct dirent entry;
    int first;
    int done;
} TernDir;

static TernDir *tern_opendir(const char *path) {
    char pattern[FILENAME_MAX];
    snprintf(pattern, sizeof pattern, "%s\\*", path);
    TernDir *dir = (TernDir *)calloc(1, sizeof(TernDir));
    if (!dir) {
        return NULL;
    }
    dir->handle = FindFirstFileA(pattern, &dir->data);
    if (dir->handle == INVALID_HANDLE_VALUE) {
        free(dir);
        return NULL;
    }
    dir->first = 1;
    return dir;
}

static struct dirent *tern_readdir(TernDir *dir) {
    if (!dir || dir->done) {
        return NULL;
    }
    if (!dir->first) {
        if (!FindNextFileA(dir->handle, &dir->data)) {
            dir->done = 1;
            return NULL;
        }
    }
    dir->first = 0;
    strncpy(dir->entry.d_name, dir->data.cFileName, sizeof dir->entry.d_name - 1);
    dir->entry.d_name[sizeof dir->entry.d_name - 1] = '\0';
    return &dir->entry;
}

static void tern_closedir(TernDir *dir) {
    if (!dir) {
        return;
    }
    FindClose(dir->handle);
    free(dir);
}

#define DIR TernDir
#define opendir(path) tern_opendir(path)
#define readdir(dir) tern_readdir(dir)
#define closedir(dir) tern_closedir(dir)

#else

#include <arpa/inet.h>
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <string.h>
#include <strings.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <locale.h>
#include <math.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <wctype.h>
#include <time.h>
#include <unistd.h>

#endif

#endif
