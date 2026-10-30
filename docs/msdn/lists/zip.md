# lists.zip

Pairs the items of several lists.

## Syntax

```tern
lists.zip(lists...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `lists` | `list` | Two or more lists. |

## Return value

- Type: `list of list`
- One entry per index, as long as the shortest list.

## Remarks

Uneven lists truncate to the shortest rather than padding with `nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | Called with fewer than two lists. |

## Example

```tern
println(lists.zip([1, 2, 3], ["a", "b"]));
```

## See also

- [lists.enumerate](enumerate.md)

---

Part of the [`lists`](index.md) namespace.
