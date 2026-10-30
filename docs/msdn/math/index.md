# math

Trigonometry, rounding, and the constants.

Angles are radians. `math.pi`, `math.tau`, `math.e`, `math.nan` and
`math.infinity` are constants, not functions: write them without parentheses.

`math.clamp` keeps the argument's type — an integer in, an integer out — while
`floor`, `ceil`, `trunc` and `round` always return a float, so that `-2.0` stays
recognisably a number from the same family. Wrap the result in `int()` when the
integer matters.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`abs`](abs.md) | 1..1 | The absolute value. |
| [`acos`](acos.md) | 1..1 | The arc cosine. |
| [`asin`](asin.md) | 1..1 | The arc sine. |
| [`atan`](atan.md) | 1..1 | The arc tangent. |
| [`atan2`](atan2.md) | 2..2 | The angle of a vector. |
| [`cbrt`](cbrt.md) | 1..1 | The cube root. |
| [`ceil`](ceil.md) | 1..1 | Rounds up to an integer. |
| [`clamp`](clamp.md) | 3..3 | Constrains a value to a range. |
| [`cos`](cos.md) | 1..1 | The cosine of an angle in radians. |
| [`e`](e.md) | 0..0 | The constant e. |
| [`exp`](exp.md) | 1..1 | Raises e to a power. |
| [`floor`](floor.md) | 1..1 | Rounds down to an integer. |
| [`gcd`](gcd.md) | 2..2 | The greatest common divisor. |
| [`hypot`](hypot.md) | 2..2 | The length of a vector. |
| [`infinity`](infinity.md) | 0..0 | Positive infinity. |
| [`isInteger`](isInteger.md) | 1..1 | True for whole numbers. |
| [`isNaN`](isNaN.md) | 1..1 | True for NaN. |
| [`lcm`](lcm.md) | 2..2 | The least common multiple. |
| [`log`](log.md) | 1..2 | The logarithm, in any base. |
| [`log10`](log10.md) | 1..1 | The base 10 logarithm. |
| [`log2`](log2.md) | 1..1 | The base 2 logarithm. |
| [`max`](max.md) | 1..n | The largest argument. |
| [`min`](min.md) | 1..n | The smallest argument. |
| [`nan`](nan.md) | 0..0 | A quiet NaN. |
| [`pi`](pi.md) | 0..0 | The constant pi. |
| [`pow`](pow.md) | 2..2 | Raises a number to a power. |
| [`round`](round.md) | 1..2 | Rounds to the nearest integer, or to n digits. |
| [`sign`](sign.md) | 1..1 | The sign of a number. |
| [`sin`](sin.md) | 1..1 | The sine of an angle in radians. |
| [`sqrt`](sqrt.md) | 1..1 | The square root. |
| [`tan`](tan.md) | 1..1 | The tangent of an angle in radians. |
| [`tau`](tau.md) | 0..0 | The constant tau, two pi. |
| [`toDegrees`](toDegrees.md) | 1..1 | Radians to degrees. |
| [`toRadians`](toRadians.md) | 1..1 | Degrees to radians. |
| [`trunc`](trunc.md) | 1..1 | Drops the fractional part. |

## Example

```tern
import "std:math";

println(math.sin(math.pi / 2));
println(math.round(2.567, 2));
println(math.clamp(15, 0, 10));
```

