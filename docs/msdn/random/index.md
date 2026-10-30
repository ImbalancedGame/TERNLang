# random

A seeded generator, for games and for tests.

One generator per engine, seeded from the clock unless `random.seed` is called.
Seeding makes a run reproducible, which is what a failing test needs.

`random.int` is inclusive at both ends. `random.choice` returns `nil` for an
empty list rather than raising, so a caller can fall back.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`bool`](bool.md) | 0..0 | A random boolean. |
| [`choice`](choice.md) | 1..1 | A random item from a list. |
| [`float`](float.md) | 0..2 | A random float in a half-open range. |
| [`int`](int.md) | 1..2 | A random integer in an inclusive range. |
| [`seed`](seed.md) | 1..1 | Seeds the generator. |
| [`shuffle`](shuffle.md) | 1..1 | Shuffles a list in place. |

## Example

```tern
import "std:random";

random.seed(1234);
println(random.int(1, 6));
println(random.choice(["rock", "paper", "scissors"]));
```

