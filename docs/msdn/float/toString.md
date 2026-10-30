# float.toString

The decimal representation.

## Syntax

```tern
float.toString()
```

`receiver` is the float on the left of the dot.

## Return value

- Type: `string`
- Shortest text that reads back as the same float; a whole value keeps its `.0`.

## Example

```tern
println(1.5.toString() + "!", (2.0).toString());
```

## See also

- [global.str](../global/str.md)

---

Part of the [`float` methods](index.md).
