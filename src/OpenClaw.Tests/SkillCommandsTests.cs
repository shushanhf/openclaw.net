using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class SkillCommandsTests
{
    [Fact]
    public async Task RunAsync_InstallDryRun_DoesNotCopySkill()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Release Notes", "Summarize release notes.");
            var exitCode = await SkillCommands.RunAsync(["install", sourceDir, "--dry-run"]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(workspace, "skills", "release-notes")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_CopiesSkillIntoWorkspaceSkills()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Inbox Triage", "Triage an inbox carefully.");
            var exitCode = await SkillCommands.RunAsync(["install", sourceDir]);

            Assert.Equal(0, exitCode);
            var installedSkillPath = Path.Combine(workspace, "skills", "inbox-triage", "SKILL.md");
            Assert.True(File.Exists(installedSkillPath));
            var installedContents = await File.ReadAllTextAsync(installedSkillPath);
            Assert.Contains("name: Inbox Triage", installedContents, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_FromTarballWithSpecialPath_Succeeds()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Tarball Skill", "Install from a tarball.");
            var tarballPath = Path.Combine(root, "-tarball skill.tgz");
            await CreateTarballAsync(root, Path.GetFileName(sourceDir), tarballPath);

            var exitCode = await SkillCommands.RunAsync(["install", tarballPath]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(workspace, "skills", "tarball-skill", "SKILL.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_RejectsSymlinkEntries()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Symlink Skill", "Should reject symlink content.");
            var outsideFile = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outsideFile, "secret");
            File.CreateSymbolicLink(Path.Combine(sourceDir, "escape.txt"), outsideFile);

            var exitCode = await SkillCommands.RunAsync(["install", sourceDir]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsPersistedMetaRunSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-1",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:00:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:00:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 42
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-1", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run: run-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Skill: meta-flow", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Status: completed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Steps: 1", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("- Step: draft | kind=llm_chat | status=completed | duration_ms=42", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unknown skills subcommand", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsStepFailureDetails()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-failed",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-err-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "search step failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:05:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:05:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "search",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 18.5
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-failed", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Run: run-err-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Error code: tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- Step: search | kind=tool_call | status=failed | duration_ms=18.5 | failure_code=tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsContinuedStepFlag()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-continued",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-cont-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "fallback ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:10:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:10:04Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    DurationMs = 7
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-continued", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("- Step: primary | kind=tool_call | status=failed | duration_ms=12 | failure_code=tool_failed | continued=true", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- Step: fallback | kind=tool_call | status=completed | duration_ms=7", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_WithNoHistory_PrintsZeroRuns()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-empty",
                    ChannelId = "cli",
                    SenderId = "tester"
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-empty", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-empty", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Meta runs: 0", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Verbose_PrintsStepSummaries()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-verbose",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-verbose-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:12:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:12:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "classify",
                                    Kind = "llm_classify",
                                    Status = "completed",
                                    DurationMs = 9
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-verbose", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("- Step: classify | kind=llm_classify | status=completed | duration_ms=9", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_RunFilter_PrintsOnlyRequestedRun()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-filter",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-older",
                            SkillName = "meta-flow",
                            Status = "failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:15:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:15:02Z")
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-target",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "selected",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:16:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:16:02Z")
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-filter", "--storage", memoryPath, "--run", "run-target"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-filter", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Meta runs: 2 total, showing 1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run: run-target", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Run: run-older", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_RunFilter_WhenRunMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-missing-run",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-present",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:20:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:20:01Z")
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-missing-run", "--storage", memoryPath, "--run", "run-absent"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Run 'run-absent' not found in session 'sess-meta-missing-run'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_PrintsFilteredRunPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-older",
                            SkillName = "meta-flow",
                            Status = "failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:25:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:25:01Z")
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-target",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "json ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:26:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:26:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 21
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json", "--storage", memoryPath, "--run", "run-json-target", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(2, rootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, rootElement.GetProperty("shownCount").GetInt32());

            var runs = rootElement.GetProperty("runs");
            Assert.Equal(1, runs.GetArrayLength());
            Assert.Equal("run-json-target", runs[0].GetProperty("runId").GetString());
            Assert.Equal("json ok", runs[0].GetProperty("finalText").GetString());
            Assert.Equal(1, runs[0].GetProperty("stepResults").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_WithNoHistory_PrintsEmptyPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json-empty",
                    ChannelId = "cli",
                    SenderId = "tester"
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json-empty", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-json-empty", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(0, rootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("shownCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("runs").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_JsonVerbose_PrintsSameStructuredPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json-verbose",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-verbose",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "json verbose ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:28:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:28:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    DurationMs = 5
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json-verbose", "--storage", memoryPath, "--json", "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var run = document.RootElement.GetProperty("runs")[0];
            Assert.Equal("run-json-verbose", run.GetProperty("runId").GetString());
            Assert.Equal(1, run.GetProperty("stepResults").GetArrayLength());
            Assert.Equal("fallback", run.GetProperty("stepResults")[0].GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_PrintsUnavailableSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-preview",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:30:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:30:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-preview", "--storage", memoryPath, "--run", "run-preview-001", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-replay-preview", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal("run-preview-001", rootElement.GetProperty("runId").GetString());
            Assert.False(rootElement.GetProperty("replayAvailable").GetBoolean());
            Assert.Contains(MetaRunReplayReasons.NotEnoughInputsForExecutableReplay, rootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
            var availableArtifacts = rootElement.GetProperty("availableArtifacts");
            Assert.Equal(2, availableArtifacts.GetArrayLength());
            Assert.Equal(MetaRunReplayArtifactNames.FinalText, availableArtifacts[0].GetString());
            Assert.Equal(MetaRunReplayArtifactNames.StepResults, availableArtifacts[1].GetString());
            var retainedSteps = rootElement.GetProperty("retainedSteps");
            Assert.Single(retainedSteps.EnumerateArray());
            Assert.Equal("draft", retainedSteps[0].GetProperty("id").GetString());
            Assert.Equal("llm_chat", retainedSteps[0].GetProperty("kind").GetString());
            Assert.Equal("completed", retainedSteps[0].GetProperty("status").GetString());
            Assert.Equal(8, retainedSteps[0].GetProperty("durationMs").GetDouble());
            Assert.False(retainedSteps[0].GetProperty("continued").GetBoolean());
            var plan = rootElement.GetProperty("plan");
            Assert.Equal(MetaRunReplayPlanSummaries.AuditableNotReplayable, plan.GetProperty("summary").GetString());
            Assert.Equal(MetaRunReplayModes.PreviewOnly, plan.GetProperty("mode").GetString());
            Assert.False(plan.GetProperty("executable").GetBoolean());
            var replayableSteps = plan.GetProperty("replayableSteps");
            Assert.Single(replayableSteps.EnumerateArray());
            Assert.Equal("draft", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.TraceOnly, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.TraceOnly, replayableSteps[0].GetProperty("reason").GetString());
            var blockedBy = plan.GetProperty("blockedByRequirements");
            Assert.Equal(3, blockedBy.GetArrayLength());
            Assert.Equal(MetaRunReplayRequirementNames.PromptContext, blockedBy[0].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[0].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.StepInputs, blockedBy[1].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[1].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.ToolArguments, blockedBy[2].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[2].GetProperty("kind").GetString());
            var missingRequirements = rootElement.GetProperty("missingRequirements");
            Assert.Equal(3, missingRequirements.GetArrayLength());
            Assert.Equal(MetaRunReplayRequirementNames.PromptContext, missingRequirements[0].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[0].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.PromptContextNotPersisted, missingRequirements[0].GetProperty("reason").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.StepInputs, missingRequirements[1].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[1].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.StepInputsNotPersisted, missingRequirements[1].GetProperty("reason").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.ToolArguments, missingRequirements[2].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[2].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.ToolArgumentsNotPersisted, missingRequirements[2].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_FailedContinuedStep_PrintsDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-json-derived-readiness",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-json-derived-readiness",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:33:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:33:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json-derived-readiness", "--storage", memoryPath, "--run", "run-preview-json-derived-readiness", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var replayableSteps = document.RootElement.GetProperty("plan").GetProperty("replayableSteps");
            Assert.Single(replayableSteps.EnumerateArray());
            Assert.Equal("primary", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.FailureTraceContinued, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.FailureTraceContinued, replayableSteps[0].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_FailedAndContinuedOnlySteps_PrintDistinctDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-json-derived-readiness-extra",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-json-derived-readiness-extra",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:35:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:35:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "failed",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 7
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "continued",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 5,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json-derived-readiness-extra", "--storage", memoryPath, "--run", "run-preview-json-derived-readiness-extra", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var replayableSteps = document.RootElement.GetProperty("plan").GetProperty("replayableSteps");
            Assert.Equal(2, replayableSteps.GetArrayLength());
            Assert.Equal("failed", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.FailureTraceOnly, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.FailureTraceOnly, replayableSteps[0].GetProperty("reason").GetString());
            Assert.Equal("continued", replayableSteps[1].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.ContinuationTraceOnly, replayableSteps[1].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.ContinuationTraceOnly, replayableSteps[1].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_PrintsUnavailableSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:31:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:31:03Z")
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text", "--storage", memoryPath, "--run", "run-preview-text"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Replay preview for run: run-preview-text", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Replay available: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(MetaRunReplayReasons.NotEnoughInputsForExecutableReplay, output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Replay plan:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Summary: {MetaRunReplayPlanSummaries.MetadataOnlyNotReplayable}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Mode: {MetaRunReplayModes.PreviewOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Executable: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Blocked by requirements: {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepResults} | kind={MetaRunReplayRequirementKinds.NotRetained} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Available artifacts:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayArtifactNames.ErrorCode}", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Retained steps:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Missing replay inputs:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.StepResults} | kind={MetaRunReplayRequirementKinds.NotRetained} | reason=", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_WithRetainedSteps_PrintsStepReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-steps",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-steps",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:32:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:32:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-steps", "--storage", memoryPath, "--run", "run-preview-text-steps"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"Summary: {MetaRunReplayPlanSummaries.AuditableNotReplayable}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Replayable steps: draft | readiness={MetaRunReplayStepReadinessKinds.TraceOnly} | reason={MetaRunReplayStepReadinessReasons.TraceOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Blocked by requirements: {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Retained steps:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_FailedContinuedStep_PrintsDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-derived-readiness",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-derived-readiness",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:34:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:34:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-derived-readiness", "--storage", memoryPath, "--run", "run-preview-text-derived-readiness"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"Replayable steps: primary | readiness={MetaRunReplayStepReadinessKinds.FailureTraceContinued} | reason={MetaRunReplayStepReadinessReasons.FailureTraceContinued}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_FailedAndContinuedOnlySteps_PrintDistinctDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-derived-readiness-extra",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-derived-readiness-extra",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:36:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:36:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "failed",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 7
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "continued",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 5,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-derived-readiness-extra", "--storage", memoryPath, "--run", "run-preview-text-derived-readiness-extra"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"failed | readiness={MetaRunReplayStepReadinessKinds.FailureTraceOnly} | reason={MetaRunReplayStepReadinessReasons.FailureTraceOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"continued | readiness={MetaRunReplayStepReadinessKinds.ContinuationTraceOnly} | reason={MetaRunReplayStepReadinessReasons.ContinuationTraceOnly}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSkill(string root, string name, string description)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        var skillDir = Path.Combine(root, slug);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---{Environment.NewLine}" +
            $"name: {name}{Environment.NewLine}" +
            $"description: {description}{Environment.NewLine}" +
            $"metadata: {{\"openclaw\":{{\"homepage\":\"https://example.com/{slug}\",\"requires\":{{\"env\":[\"OPENAI_API_KEY\"]}}}}}}{Environment.NewLine}" +
            $"---{Environment.NewLine}{Environment.NewLine}" +
            "Follow the documented process." +
            Environment.NewLine);
        return skillDir;
    }

    private static async Task CreateTarballAsync(string workingDirectory, string sourceDirectoryName, string tarballPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--create");
        process.StartInfo.ArgumentList.Add("--gzip");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(tarballPath);
        process.StartInfo.ArgumentList.Add(sourceDirectoryName);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"tar create failed: {stderr}");
    }
}
