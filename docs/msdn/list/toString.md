# list.toString

A debug rendering of the list.

## Syntax

```tern
list.toString()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `string`
- Brackets, items separated by `, `, strings quoted.

## Example

```tern
println([1, "a"].toString());
```

## See also

- [global.repr](../global/repr.md)
- [list.join](join.md)

---

Part of the [`list` methods](index.md).
