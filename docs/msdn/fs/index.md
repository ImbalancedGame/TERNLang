# fs

Files, directories and reading a file in pieces.

`fs.read` loads a whole file, which is the wrong shape for a log that is still
growing or an archive that is four gigabytes. `fs.open` returns a handle that
keeps its position, `fs.readChunk` returns the next piece, and `fs.readAt` does
the same read without a handle. Chunks are byte-addressed and never split a
UTF-8 character, so they concatenate back into the file.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`append`](append.md) | 2..2 | Appends text, creating the file if it is missing. |
| [`close`](close.md) | 1..1 | Closes a file handle. |
| [`exists`](exists.md) | 1..1 | True when a file or directory exists. |
| [`fileSize`](fileSize.md) | 1..1 | Size of an open file in bytes. |
| [`isDir`](isDir.md) | 1..1 | True for a directory. |
| [`isFile`](isFile.md) | 1..1 | True for a regular file. |
| [`list`](list.md) | 1..1 | Directory entries as a list of names. |
| [`mkdir`](mkdir.md) | 1..1 | Creates a directory and its parents. |
| [`open`](open.md) | 1..1 | Opens a file for reading and returns a handle that keeps its position. |
| [`position`](position.md) | 1..1 | The byte offset the next read starts at. |
| [`read`](read.md) | 1..1 | Reads a whole file as text. |
| [`readAt`](readAt.md) | 2..3 | Reads up to n bytes at a byte offset, without a handle. |
| [`readBytes`](readBytes.md) | 1..1 | Reads a whole file as a list of bytes. |
| [`readChunk`](readChunk.md) | 2..2 | Reads up to n bytes from a handle's position and advances it. |
| [`remove`](remove.md) | 1..1 | Deletes a file or a directory tree. |
| [`seek`](seek.md) | 2..2 | Moves a handle's position and returns it. |
| [`size`](size.md) | 1..1 | Size of a file in bytes. |
| [`write`](write.md) | 2..2 | Writes text, replacing the file and creating its directories. |

## Example

```tern
import "std:fs";

let file = fs.open("/var/log/huge.log");
fs.seek(file, 512 * 1024 * 1024);
let chunk = fs.readChunk(file, 10 * 1024 * 1024);
println(len(chunk), fs.position(file));
fs.close(file);
```

