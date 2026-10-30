# list.shift

Removes and returns the first item.

## Syntax

```tern
list.shift()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `any`
- The item, or `nil` when the list is empty.

## Example

```tern
let xs = [1, 2];
println(xs.shift(), xs);
```

## See also

- [list.pop](pop.md)

---

Part of the [`list` methods](index.md).
