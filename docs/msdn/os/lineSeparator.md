# os.lineSeparator

The platform's line separator.

## Syntax

```tern
os.lineSeparator()
```

## Parameters

_None._

## Return value

- Type: `string`
- `\n` everywhere except Windows, where it is `\r\n`.

## Example

```tern
println(len(os.lineSeparator));
```

## See also

- [os.platform](platform.md)

---

Part of the [`os`](index.md) namespace.
