// This file intentionally contains no entry point.
//
// HoardPad.AcceptanceHarness is a TEST project (xUnit) — the SC-5 acceptance gate
// (architecture §1.6, M9.3). The M0.1 scaffold's `Main` was removed when the project
// was wired as a test project: a pure test project has no `OutputType=Exe`, so the
// SC-5(i)/(ia)/no-re-arm gates run as [Fact]/[Theory] tests via `dotnet test --filter`.
//
// The gates live in:
//   - Sc5HookGateTests.cs        — SC-5(i): drive the REAL python deny_by_default.py hook
//   - Sc5SharedServiceTests.cs   — SC-5(ia): drive ops through the real AiFileOpsService
//   - NoReArmTests.cs            — R5-BL1: real CapStateStore across a simulated continuation
//   - CrossLanguageBoundaryTests.cs — FR-38/FR-40 C# vs python agreement
//   - SandboxGateE2ETests.cs     — production PathResolverSandboxGate end-to-end
//   - DurabilityCrashTests.cs    — torn-tail append-log repair
//
// Shared composition + the hook subprocess driver live in HarnessSupport.cs.

namespace HoardPad.AcceptanceHarness;
