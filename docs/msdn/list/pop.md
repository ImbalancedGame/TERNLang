# list.pop

Removes and returns the last item.

## Syntax

```tern
list.pop()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `any`
- The item, or `nil` when the list is empty.

## Example

```tern
let xs = [1, 2];
println(xs.pop(), xs);
```

## See also

- [list.shift](shift.md)
- [list.remove](remove.md)

---

Part of the [`list` methods](index.md).
