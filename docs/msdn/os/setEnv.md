# os.setEnv

Sets an environment variable for this process.

## Syntax

```tern
os.setEnv(name, value)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `name` | `string` | The variable. |
| `value` | `string` | The value; `\"\"` removes it. |

## Return value

`nil`.

## Remarks

The change is visible to this process and to programs it starts, not to the shell it was started from.

## Errors

| Error | Raised when |
| --- | --- |
| `IOError` | The platform refuses the change. |

## Example

```tern
os.setEnv("TERN_TEST", "1");
println(os.env("TERN_TEST"));
```

## See also

- [os.env](env.md)

---

Part of the [`os`](index.md) namespace.
