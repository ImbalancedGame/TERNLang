# time

The clock, and sleeping.

Two clocks are exposed because two questions are asked: `time.millis` measures a
duration (a monotonic-enough count of milliseconds since the epoch), and
`time.iso` asks what the date is. `time.now` is the epoch in seconds as a float,
which is the form most protocols want.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`iso`](iso.md) | 1..1 | A time as an ISO 8601 string. |
| [`millis`](millis.md) | 0..0 | Milliseconds since the Unix epoch. |
| [`now`](now.md) | 0..0 | Seconds since the Unix epoch. |
| [`sleep`](sleep.md) | 1..1 | Suspends the script for a number of seconds. |

## Example

```tern
import "std:time";

let started = time.millis();
time.sleep(0.05);
println(time.millis() - started >= 50);
println(time.iso(time.now()));
```

