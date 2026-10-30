# random.shuffle

Shuffles a list in place.

## Syntax

```tern
random.shuffle(list)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `list` | `list` | The list; changed in place. |

## Return value

- Type: `list`
- The same list, for chaining.

## Example

```tern
let deck = [1, 2, 3, 4];
random.shuffle(deck);
println(len(deck));
```

## See also

- [random.choice](choice.md)
- [list.sort](../list/sort.md)

---

Part of the [`random`](index.md) namespace.
