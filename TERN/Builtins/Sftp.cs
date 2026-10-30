using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// <c>sftp</c>: file transfer over SSH. There is no SFTP library to link here,
    /// and writing one means implementing SSH transport by hand, so this drives
    /// the <c>sftp</c> program that ships with OpenSSH in batch mode: a batch of
    /// commands on standard input, the answer on standard output, and no
    /// interactive prompts (BatchMode=yes fails instead of asking for a password).
    ///
    /// The batch text and the command line are exposed as <c>sftp.batch</c> and
    /// <c>sftp.command</c>, which is what the tests pin down and what a script
    /// prints when it wants to see what would run.
    /// </summary>
    internal static class SftpModule
    {
        private const int DefaultTimeout = 30000;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();

            Add(map, "upload", 2, 3, (vm, a) =>
            {
                var local = Text(vm, a[0], "local");
                var remote = Text(vm, a[1], "remote");
                var options = Parse(vm, a.Length > 2 ? a[2] : Value.Nil, "upload");
                RequireLocal(vm, local, "local");
                var result = Run(vm, options, new[] { $"put {Quote(local)} {Quote(remote)}" });
                RequireSuccess(vm, result);
                return Value.Nil;
            }, "Copies a local file to the server: sftp.upload(local, remote[, options]).");

            Add(map, "download", 2, 3, (vm, a) =>
            {
                var remote = Text(vm, a[0], "remote");
                var local = Text(vm, a[1], "local");
                var options = Parse(vm, a.Length > 2 ? a[2] : Value.Nil, "download");
                var result = Run(vm, options, new[] { $"get {Quote(remote)} {Quote(local)}" });
                RequireSuccess(vm, result);
                return Value.Nil;
            }, "Copies a file from the server: sftp.download(remote, local[, options]).");

            Add(map, "list", 1, 2, (vm, a) =>
            {
                var remote = Text(vm, a[0], "remote");
                var options = Parse(vm, a.Length > 1 ? a[1] : Value.Nil, "list");
                var result = Run(vm, options, new[] { $"ls -1 {Quote(remote)}" });
                RequireSuccess(vm, result);

                var names = new TernList();
                foreach (var line in result.Output.Split('\n'))
                {
                    var name = line.TrimEnd('\r');
                    if (name.Length > 0)
                    {
                        names.Add(Value.Str(name));
                    }
                }

                return Value.Of(names);
            }, "Lists a remote directory: sftp.list(remote[, options]).");

            Add(map, "run", 2, 2, (vm, a) =>
            {
                var commands = Commands(vm, a[0]);
                var options = Parse(vm, a[1], "run");
                var result = Run(vm, options, commands);

                var report = new TernMap();
                report.Set(Value.Str("status"), new Value(result.Status));
                report.Set(Value.Str("output"), Value.Str(result.Output));
                report.Set(Value.Str("error"), Value.Str(result.Error));
                return Value.Of(report);
            }, "Runs batch commands and reports {status, output, error} without raising.");

            Add(map, "batch", 1, 1, (vm, a) => Value.Str(BatchText(Commands(vm, a[0]))),
                "The batch file text for a list of commands.");

            Add(map, "command", 1, 1, (vm, a) =>
            {
                var options = Parse(vm, a[0], "command");
                var list = new TernList();
                foreach (var part in Command(options))
                {
                    list.Add(Value.Str(part));
                }

                return Value.Of(list);
            }, "The command line sftp is started with, as a list of strings.");

            engine.RegisterValue("sftp", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:sftp",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string documentation) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        // ----------------------------------------------------------------- options

        private sealed class Options
        {
            public string Host { get; set; } = string.Empty;
            public string User { get; set; } = string.Empty;
            public string? Key { get; set; }
            public int Port { get; set; } = 22;
            public int Timeout { get; set; } = DefaultTimeout;
        }

        private static Options Parse(VM vm, in Value value, string operation)
        {
            if (value.Kind != ValueKind.Map)
            {
                throw vm.Error("TypeError", $"{operation}() options must be a map, not {vm.TypeName(value)}");
            }

            var map = value.AsMap();
            var options = new Options();

            if (map.TryGet(Value.Str("host"), out var host))
            {
                options.Host = Text(vm, host, "option 'host'");
            }

            if (map.TryGet(Value.Str("user"), out var user))
            {
                options.User = Text(vm, user, "option 'user'");
            }

            if (map.TryGet(Value.Str("key"), out var key))
            {
                options.Key = Text(vm, key, "option 'key'");
            }

            if (map.TryGet(Value.Str("port"), out var port))
            {
                var number = NetModule.Number(vm, port, "option 'port'");
                if (number < 1 || number > 65535)
                {
                    throw vm.Error("ArgumentError", "option 'port' must be between 1 and 65535");
                }

                options.Port = (int)number;
            }

            if (map.TryGet(Value.Str("timeout"), out var timeout))
            {
                var milliseconds = NetModule.Number(vm, timeout, "option 'timeout'");
                if (milliseconds < 0)
                {
                    throw vm.Error("ArgumentError", "option 'timeout' must not be negative");
                }

                options.Timeout = (int)milliseconds;
            }

            if (options.Host.Length == 0)
            {
                throw vm.Error("ArgumentError", "option 'host' is required");
            }

            if (options.User.Length == 0)
            {
                throw vm.Error("ArgumentError", "option 'user' is required");
            }

            return options;
        }

        private static string Text(VM vm, in Value value, string what) => NetModule.Text(vm, value, what);

        private static List<string> Commands(VM vm, in Value value)
        {
            if (value.Kind != ValueKind.List)
            {
                throw vm.Error("TypeError", $"expected a list of commands, not {vm.TypeName(value)}");
            }

            var list = value.AsList();
            var commands = new List<string>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Kind != ValueKind.Str)
                {
                    throw vm.Error("TypeError", $"command {i} must be a string, not {vm.TypeName(list[i])}");
                }

                var command = list[i].AsString();
                if (command.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                {
                    throw vm.Error("SftpError", $"command {i} contains a line break");
                }

                commands.Add(command);
            }

            return commands;
        }

        /// <summary>
        /// A path with a space would split into two arguments, and sftp has no
        /// escaping of its own, so it is quoted the way the batch parser expects.
        /// </summary>
        private static string Quote(string path) =>
            path.IndexOfAny(new[] { ' ', '\t' }) < 0 ? path : "\"" + path.Replace("\"", "\\\"") + "\"";

        private static string BatchText(List<string> commands)
        {
            var text = new StringBuilder();
            foreach (var command in commands)
            {
                text.Append(command).Append('\n');
            }

            return text.ToString();
        }

        private static List<string> Command(Options options)
        {
            var argv = new List<string> { "sftp", "-b", "-", "-P", options.Port.ToString() };
            if (options.Key is not null)
            {
                argv.Add("-i");
                argv.Add(options.Key);
            }

            argv.Add("-o");
            argv.Add("BatchMode=yes");
            argv.Add("-o");
            argv.Add("StrictHostKeyChecking=accept-new");
            argv.Add($"{options.User}@{options.Host}");
            return argv;
        }

        private static void RequireLocal(VM vm, string path, string what)
        {
            if (!File.Exists(path))
            {
                throw vm.Error("SftpError", $"{what} file '{path}' does not exist");
            }
        }

        // ------------------------------------------------------------------ running

        private readonly struct Result
        {
            public Result(int status, string output, string error)
            {
                Status = status;
                Output = output;
                Error = error;
            }

            public int Status { get; }
            public string Output { get; }
            public string Error { get; }
        }

        private static Result Run(VM vm, Options options, IReadOnlyList<string> commands)
        {
            NetModule.RequireNetwork(vm);
            if (options.Key is not null && !File.Exists(options.Key))
            {
                throw vm.Error("SftpError", $"cannot read the key file '{options.Key}'");
            }

            var startInfo = new ProcessStartInfo("sftp")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in Command(options))
            {
                startInfo.ArgumentList.Add(argument);
            }

            // The program name is already in Command(); ProcessStartInfo wants it
            // separately, so the first entry is dropped by starting with it.
            startInfo.FileName = startInfo.ArgumentList[0];
            startInfo.ArgumentList.RemoveAt(0);

            Process process;
            try
            {
                process = Process.Start(startInfo) ?? throw new IOException("sftp did not start");
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
            {
                throw vm.Error("SftpError", "cannot run sftp: is the OpenSSH client installed?");
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    process.StandardInput.Write(BatchText(new List<string>(commands)));
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The process may exit before reading its input; the status says why.
                }

                if (!process.WaitForExit(options.Timeout))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone.
                    }

                    throw vm.Error("SftpError", $"sftp timed out after {options.Timeout} ms");
                }

                return new Result(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            }
        }

        private static void RequireSuccess(VM vm, Result result)
        {
            if (result.Status == 0)
            {
                return;
            }

            var message = FirstLine(result.Error);
            throw vm.Error("SftpError", message is null
                ? $"sftp failed with status {result.Status}"
                : $"sftp failed with status {result.Status}: {message}");
        }

        private static string? FirstLine(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return null;
        }
    }
}
