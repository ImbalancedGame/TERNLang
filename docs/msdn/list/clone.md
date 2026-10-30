# list.clone

A shallow copy.

## Syntax

```tern
list.clone()
```

`receiver` is the list on the left of the dot.

## Return value

- Type: `list`
- A new list with the same items.

## Remarks

The items are shared: a nested list in the copy is the same nested list.

## Example

```tern
let a = [1]; let b = a.clone(); b.push(2); println(len(a), len(b));
```

## See also

- [global.copy](../global/copy.md)

---

Part of the [`list` methods](index.md).
