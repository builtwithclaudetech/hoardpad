// Program.cs — HoardPad.SelfTestStandIn (STF-3, Option 1).
//
// A tiny, purpose-built process ToolPermissionHookHost's two self-tests launch INSTEAD OF the
// real claude CLI (docs/stf-diagnosis.md root cause: the real CLI has no knowledge of either
// descriptor protocol and can never drive the crafted event through the hook). It reproduces, in
// C#, the exact behavior already proven correct by the Python reference stand-ins:
//   - fail-closed mode mirrors HookHostTests.cs's FaithfulStandIn.
//   - channel-kill mode mirrors ChannelKillSelfTestTests.cs's FaithfulHookOnKilledChannel.
//
// This process is SELF-TEST-ONLY — never on the production turn-launch path. It never grants an
// out-of-sandbox write on its own authority: it forwards the crafted event to the REAL, bundled
// deny_by_default.py hook (fail-closed mode) or reads the REAL observation channel (channel-kill
// mode) and honors whatever that hook/channel decides, exactly as the Python stand-ins do.
//
// MODE DETECTION: ToolPermissionHookHost.cs is spine-adjacent and BYTE-UNMODIFIED by STF-3 — its
// two self-test methods build argv exactly as they always have (`--settings <path> <descriptor>`
// for the fail-closed gate; a bare `<descriptor>` for the channel-kill gate, see
// RunFailClosedSelfTestAsync / RunChannelKillSelfTestAsync). This process therefore detects its
// mode from argv SHAPE alone (no new leading mode token, since that would require editing the
// spine-adjacent host): the presence of a leading `--settings` flag selects fail-closed mode;
// its absence (a single positional descriptor-path argument) selects channel-kill mode.

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HoardPad.SelfTestStandIn;

internal static class Program
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: hoardpad-selftest-standin [--settings <path>] <descriptorPath>");
            return 1;
        }

        return args.Contains("--settings")
            ? RunFailClosed(args)
            : RunChannelKill(args);
    }

    // ---- fail-closed mode: mirrors HookHostTests.cs's FaithfulStandIn ----
    //
    // Invoked as: --settings <settingsFile> <descriptorPath>
    // Reads the --settings JSON ToolPermissionHookHost.BuildHookSettings produced, extracts the
    // hook command it wired, invokes THAT command (the real bundled deny_by_default.py) with the
    // crafted out-of-sandbox Write event on stdin, and honors the verdict: deny => write nothing,
    // record the denial; allow (or an unreadable/missing hook command) => write the escape file so
    // the host detects the disarmed/broken-wiring state.

    private static int RunFailClosed(string[] args)
    {
        string? settingsPath = null;
        string? descriptorPath = null;
        int i = 0;
        while (i < args.Length)
        {
            if (args[i] == "--settings" && i + 1 < args.Length)
            {
                settingsPath = args[i + 1];
                i += 2;
            }
            else
            {
                descriptorPath = args[i];
                i += 1;
            }
        }

        if (settingsPath is null || descriptorPath is null)
        {
            Console.Error.WriteLine("fail-closed mode requires --settings <path> <descriptorPath>.");
            return 1;
        }

        using JsonDocument descriptor = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        JsonElement d = descriptor.RootElement;
        string sandboxRoot = d.GetProperty("sandboxRoot").GetString()!;
        string escapeTarget = d.GetProperty("escapeTarget").GetString()!;
        string denialRecordPath = d.GetProperty("denialRecordPath").GetString()!;
        string capStateFile = d.GetProperty("capStateFile").GetString()!;
        string toolName = d.GetProperty("toolName").GetString()!;
        string content = d.GetProperty("content").GetString()!;

        string? hookCommand = TryReadHookCommand(settingsPath);

        string decision = "deny";   // default to the safe reading, matching FaithfulStandIn.
        if (hookCommand is not null)
        {
            decision = InvokeHookCommand(hookCommand, toolName, escapeTarget, content, sandboxRoot, capStateFile);
        }
        // else: no hook command found in --settings => the wiring is broken => disarmed (allow stays).

        if (decision == "deny")
        {
            string record = JsonSerializer.Serialize(new
            {
                permission_denials = new[] { new { tool = toolName, path = escapeTarget } },
            });
            File.WriteAllText(denialRecordPath, record);
        }
        else
        {
            File.WriteAllText(escapeTarget, content);
        }

        return 0;
    }

    private static string? TryReadHookCommand(string settingsPath)
    {
        try
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!settings.RootElement.TryGetProperty("hooks", out JsonElement hooks))
                return null;
            if (!hooks.TryGetProperty("PreToolUse", out JsonElement preToolUse))
                return null;
            foreach (JsonElement entry in preToolUse.EnumerateArray())
            {
                if (!entry.TryGetProperty("hooks", out JsonElement innerHooks))
                    continue;
                foreach (JsonElement h in innerHooks.EnumerateArray())
                {
                    if (h.TryGetProperty("type", out JsonElement type) && type.GetString() == "command" &&
                        h.TryGetProperty("command", out JsonElement command))
                    {
                        return command.GetString();
                    }
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;   // settings unreadable => treat as disarmed (the caller's default "deny" holds moot; see below).
        }
    }

    /// <summary>
    /// Invoke the hook command exactly as the CLI would: run it through the platform shell with the
    /// crafted event JSON on stdin, and read its permission decision from stdout (or exit code 2).
    /// Any invocation failure is treated as a failed/disarmed hook, matching FaithfulStandIn's "allow"
    /// fallback (fail-safe: the host detects the resulting escape file).
    /// </summary>
    private static string InvokeHookCommand(
        string hookCommand, string toolName, string targetPath, string content, string sandboxRoot, string capStateFile)
    {
        var eventJson = JsonSerializer.Serialize(new
        {
            tool_name = toolName,
            tool_input = new { file_path = targetPath, content },
        });

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(hookCommand);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(hookCommand);
        }
        psi.Environment["HOARDPAD_SANDBOX_ROOT"] = sandboxRoot;
        psi.Environment["HOARDPAD_CAP_STATE_FILE"] = capStateFile;

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc is null)
                return "allow";   // could not launch the hook => disarmed (fail-safe: host detects the escape file).

            proc.StandardInput.Write(eventJson);
            proc.StandardInput.Close();
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();   // drain (avoid a pipe-full deadlock); content unused.
            proc.WaitForExit();

            if (proc.ExitCode == 0 && stdout.Length > 0)
            {
                try
                {
                    using JsonDocument outDoc = JsonDocument.Parse(stdout);
                    if (outDoc.RootElement.TryGetProperty("hookSpecificOutput", out JsonElement hso) &&
                        hso.TryGetProperty("permissionDecision", out JsonElement pd))
                    {
                        return pd.GetString() == "allow" ? "allow" : "deny";
                    }
                    return "deny";
                }
                catch (JsonException)
                {
                    return "deny";
                }
            }
            if (proc.ExitCode == 2)
                return "deny";
            return "allow";   // non-zero, non-2 exit from the hook: treat as a failed invocation => disarmed.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return "allow";   // hook invocation raised => treat as disarmed (hook not reachable).
        }
    }

    // ---- channel-kill mode: mirrors ChannelKillSelfTestTests.cs's FaithfulHookOnKilledChannel ----
    //
    // Invoked as: <descriptorPath>  (no --settings flag — that is how Main tells the modes apart).
    // Connects to HOARDPAD_OBSERVE_CHANNEL (an AF_UNIX socket on Linux), sends an `ask`, writes the
    // `<descriptorPath>.connected` marker (the host's cue to kill the channel), then BLOCKS reading
    // the `decision` line. On a closed channel (EOF / reset / any error) it DENIES per protocol §4:
    // writes nothing at the target, records the denial.

    private static int RunChannelKill(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("channel-kill mode requires <descriptorPath>.");
            return 1;
        }

        string descriptorPath = args[0];
        using JsonDocument descriptor = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        JsonElement d = descriptor.RootElement;
        string writeTarget = d.GetProperty("writeTarget").GetString()!;
        string denialRecordPath = d.GetProperty("denialRecordPath").GetString()!;
        string toolUseId = d.GetProperty("toolUseId").GetString()!;
        string content = d.GetProperty("content").GetString()!;

        string? channelAddress = Environment.GetEnvironmentVariable("HOARDPAD_OBSERVE_CHANNEL");
        if (string.IsNullOrEmpty(channelAddress))
        {
            Deny(denialRecordPath, writeTarget, "channel-address-missing");
            return 0;
        }

        // linux-x64 only (the acceptance platform, m11-channel-protocol.md): AF_UNIX SOCK_STREAM.
        // The win-x64 named-pipe leg is runtime-Deferred throughout the AI spine; this stand-in
        // follows the same posture — it is exercised on Linux, compiles for Windows unexercised.
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(channelAddress));
        }
        catch (SocketException)
        {
            Deny(denialRecordPath, writeTarget, "channel-unreachable");
            return 0;
        }

        using var stream = new NetworkStream(socket, ownsSocket: false);

        try
        {
            string ask = JsonSerializer.Serialize(new
            {
                type = "ask",
                tool_use_id = toolUseId,
                tool_name = "Write",
                target_path = writeTarget,
                op_class = "create",
            });
            byte[] askBytes = Utf8NoBom.GetBytes(ask + "\n");
            stream.Write(askBytes);
            stream.Flush();
        }
        catch (IOException)
        {
            Deny(denialRecordPath, writeTarget, "ask-write-failed");
            return 0;
        }

        // Signal the host that the ask is in flight; the host now kills the channel.
        File.WriteAllText(descriptorPath + ".connected", "connected");

        // BLOCK reading the decision line. On the killed channel this returns EOF or raises => DENY.
        string? line = TryReadLine(stream);
        if (line is null)
        {
            Deny(denialRecordPath, writeTarget, "channel-closed");
            return 0;
        }

        try
        {
            using JsonDocument decisionDoc = JsonDocument.Parse(line);
            JsonElement decision = decisionDoc.RootElement;
            bool isAllow =
                decision.TryGetProperty("type", out JsonElement type) && type.GetString() == "decision" &&
                decision.TryGetProperty("tool_use_id", out JsonElement id) && id.GetString() == toolUseId &&
                decision.TryGetProperty("allow", out JsonElement allow) &&
                allow.ValueKind == JsonValueKind.True;
            if (isAllow)
            {
                File.WriteAllText(writeTarget, content);
                return 0;
            }
        }
        catch (JsonException)
        {
            // fall through to deny.
        }

        Deny(denialRecordPath, writeTarget, "explicit-deny-or-malformed");
        return 0;
    }

    private static string? TryReadLine(Stream stream)
    {
        var buffer = new MemoryStream();
        Span<byte> readByte = stackalloc byte[1];
        try
        {
            while (true)
            {
                int read = stream.Read(readByte);
                if (read == 0)
                    return buffer.Length == 0 ? null : null;   // EOF before a newline => no decision.
                if (readByte[0] == (byte)'\n')
                    return Utf8NoBom.GetString(buffer.ToArray());
                buffer.WriteByte(readByte[0]);
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Deny(string denialRecordPath, string writeTarget, string reason)
    {
        string record = JsonSerializer.Serialize(new
        {
            permission_denials = new[] { new { tool = "Write", path = writeTarget, reason } },
        });
        File.WriteAllText(denialRecordPath, record);
    }
}
