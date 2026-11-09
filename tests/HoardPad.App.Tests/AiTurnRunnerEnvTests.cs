// AiTurnRunnerEnvTests.cs — the M11 launch-env seam, App plumbing half (Option A).
//
// Proves AiTurnRunner threads its constructor launchEnv VERBATIM into IAgentBackend.StartTurnAsync —
// i.e. the env the binder assembled (HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE + the optional
// HOARDPAD_OBSERVE_* channel vars) reaches the launch seam. The runner adds no safety logic; this is
// pure env-plumbing, so a capturing backend (no real claude) is the right probe. A regression that
// dropped the env, passed an empty map, or never wired the ctor param breaks THIS test (non-vacuous).

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiTurnRunnerEnvTests
{
    [Fact]
    public async Task RunAsyncThreadsTheLaunchEnvVerbatimIntoStartTurnAsync()
    {
        var backend = new CapturingAgentBackend();
        NormcasedPath root = new HoardPad.Host.PathResolver().ResolveCanonical(
            System.IO.Path.GetTempPath(), System.IO.Path.GetTempPath());

        var launchEnv = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["HOARDPAD_SANDBOX_ROOT"] = "/the/root",
            ["HOARDPAD_CAP_STATE_FILE"] = "/appdata/caps/abc.capstate",
            ["HOARDPAD_OBSERVE_CHANNEL"] = "/run/hp-observe.sock",
            ["HOARDPAD_OBSERVE_MODE"] = "approve",
        };

        var runner = new AiTurnRunner(backend, root, settingsJsonPath: "/tmp/s.json", maxBudgetUsd: 5m, launchEnv);

        // Drive one turn; the fake backend yields an empty stream so RunAsync completes immediately.
        await foreach (TurnEvent _ in runner.RunAsync(
            new TurnRequest("hello", AgencyMode.AutoApply, WriteAgencyArmed: false), CancellationToken.None))
        {
        }

        Assert.NotNull(backend.CapturedEnv);
        // The exact env the binder assembled reached the launch seam — verbatim, no key dropped/renamed.
        Assert.Equal(launchEnv, backend.CapturedEnv);
    }
}
