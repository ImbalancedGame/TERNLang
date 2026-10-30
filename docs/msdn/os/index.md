# os

Arguments, environment, paths and the platform.

`os.args` holds the arguments after the script path. `os.joinPath` uses the
platform's separator, so a path built here runs on the machine that built it and
nowhere else; a path that has to travel belongs in a config file, not in `joinPath`.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`args`](args.md) | 0..0 | The script's arguments. |
| [`cwd`](cwd.md) | 0..0 | The current working directory. |
| [`env`](env.md) | 1..2 | Reads an environment variable. |
| [`joinPath`](joinPath.md) | 0..n | Joins path segments with the platform separator. |
| [`lineSeparator`](lineSeparator.md) | 0..0 | The platform's line separator. |
| [`platform`](platform.md) | 0..0 | The host platform name. |
| [`setEnv`](setEnv.md) | 2..2 | Sets an environment variable for this process. |

## Example

```tern
import "std:os";

println(os.platform, os.cwd());
println(os.joinPath("build", "out", "app.png"));
println(os.env("HOME", "(unset)"));
```

