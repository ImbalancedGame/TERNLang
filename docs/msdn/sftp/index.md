# sftp

File transfer over SSH, by driving the OpenSSH client.

SFTP is a subsystem inside an SSH connection, and SSH is cryptography end to
end, so rather than link an SSH library — which would stop compiled programs from
being self-contained — `sftp` runs the OpenSSH `sftp` program in batch mode. This
is the only place TERN starts an external program.

The batch runs with `BatchMode=yes`, so a missing key fails immediately instead
of prompting, and `StrictHostKeyChecking=accept-new`, so an unknown host is
accepted once and a changed key is still refused.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`batch`](batch.md) | 1..1 | The batch file text for a list of commands. |
| [`command`](command.md) | 1..1 | The command line `sftp` is started with. |
| [`download`](download.md) | 2..3 | Copies a file from the server. |
| [`list`](list.md) | 1..2 | Lists a remote directory. |
| [`run`](run.md) | 2..2 | Runs batch commands and reports the outcome without raising. |
| [`upload`](upload.md) | 2..3 | Copies a local file to the server. |

## Example

```tern
import "std:sftp";

let server = {"host": "example.com", "user": "deploy", "key": "/home/me/.ssh/id_ed25519"};
sftp.upload("build/app.tar.gz", "/srv/app.tar.gz", server);
println(sftp.list("/srv", server));
```

