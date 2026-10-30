# map.toString

A debug rendering of the map.

## Syntax

```tern
map.toString()
```

`receiver` is the map on the left of the dot.

## Return value

- Type: `string`
- Braces, `key: value` pairs, strings quoted.

## Example

```tern
println({"a": 1}.toString());
```

## See also

- [global.repr](../global/repr.md)

---

Part of the [`map` methods](index.md).
