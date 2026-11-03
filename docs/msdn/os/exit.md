# os.exit

Ends the script with a status.

## Syntax

```tern
os.exit(code?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `code` | `int` | Optional status, 0 by default: 0 for success, anything else for failure. |

## Return value

- Type: `nil`
- Never returns: the process ends.

## Remarks

The status is what the shell, the IDE and a build step see, so a script that did not do its job should exit non-zero rather than print a message and return 0. `exit` is not an error and cannot be caught: a `try` block around it does not run, which is what a script that really means to stop needs. Output written so far is flushed first, in both back ends.

## Errors

| Error | Raised when |
| --- | --- |
| `RangeError` | The status is not between 0 and 255. |

## Example

```tern
if (options == nil) { println("nothing to do"); os.exit(2); }
```

## See also

- [cli.parse](../cli/parse.md)

---

Part of the [`os`](index.md) namespace.
