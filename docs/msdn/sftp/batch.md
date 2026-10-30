# sftp.batch

The batch file text for a list of commands.

## Syntax

```tern
sftp.batch(commands)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `commands` | `list of string` | One line per command. |

## Return value

- Type: `string`
- The text, newline terminated.

## Remarks

Pure. Useful for printing exactly what would run.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The argument is not a list, or an item is not a string. |

## Example

```tern
print(sftp.batch(["put a.txt b.txt"]));
```

## See also

- [sftp.command](command.md)
- [sftp.run](run.md)

---

Part of the [`sftp`](index.md) namespace.
