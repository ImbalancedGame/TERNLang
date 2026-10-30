# os.env

Reads an environment variable.

## Syntax

```tern
os.env(name, fallback?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | The variable. |
| `fallback` | `any` | Optional; returned when the variable is unset. |

## Return value

- Type: `string|any`
- The value, or the fallback (`nil` when none was given).

## Example

```tern
println(os.env("TERM", "(none)"));
```

## See also

- [os.setEnv](setEnv.md)

---

Part of the [`os`](index.md) namespace.
