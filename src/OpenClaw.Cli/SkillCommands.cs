using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Cli;

internal static class SkillCommands
{
    private const string EnvWorkspace = "OPENCLAW_WORKSPACE";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToArray();

        return subcommand switch
        {
            "inspect" => await InspectAsync(rest),
            "install" => await InstallAsync(rest),
            "list" or "ls" => ListInstalled(rest),
            "meta-runs" => await ListMetaRunsAsync(rest),
            _ => UnknownSubcommand(subcommand)
        };
    }

    private static async Task<int> ListMetaRunsAsync(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
            return await PreviewMetaRunReplayAsync(args.Skip(1).ToArray());

        var asJson = args.Contains("--json");
        var verbose = args.Contains("--verbose");
        var requestedRunId = GetOptionValue(args, "--run");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Console.Error.WriteLine("Usage: openclaw skills meta-runs <session-id> [--storage <path>] [--limit <count>] [--run <run-id>] [--verbose] [--json]");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var limit = ParseIntOption(args, "--limit") ?? 20;
        limit = Math.Clamp(limit, 1, 100);

        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                Console.Error.WriteLine($"Session '{sessionId}' not found.");
                return 1;
            }

            if (session.MetaRunHistory.Count == 0)
            {
                if (asJson)
                {
                    WriteMetaRunsJson(sessionId, session.MetaRunHistory.Count, []);
                    return 0;
                }

                Console.WriteLine($"Session: {sessionId}");
                Console.WriteLine("Meta runs: 0");
                return 0;
            }

            var runs = session.MetaRunHistory
                .Where(run => string.IsNullOrWhiteSpace(requestedRunId) || string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal))
                .OrderByDescending(static run => run.CompletedAtUtc)
                .Take(limit)
                .ToArray();

            if (!string.IsNullOrWhiteSpace(requestedRunId) && runs.Length == 0)
            {
                Console.Error.WriteLine($"Run '{requestedRunId}' not found in session '{sessionId}'.");
                return 1;
            }

            if (asJson)
            {
                WriteMetaRunsJson(sessionId, session.MetaRunHistory.Count, runs);
                return 0;
            }

            Console.WriteLine($"Session: {sessionId}");
            Console.WriteLine($"Meta runs: {session.MetaRunHistory.Count} total, showing {runs.Length}");
            Console.WriteLine();

            foreach (var run in runs)
            {
                Console.WriteLine($"Run: {run.RunId}");
                Console.WriteLine($"Skill: {run.SkillName}");
                Console.WriteLine($"Status: {run.Status}");
                Console.WriteLine($"Started: {run.StartedAtUtc:O}");
                Console.WriteLine($"Completed: {run.CompletedAtUtc:O}");
                Console.WriteLine($"Steps: {run.StepResults.Count}");
                if (!string.IsNullOrWhiteSpace(run.ErrorCode))
                    Console.WriteLine($"Error code: {run.ErrorCode}");
                if (!string.IsNullOrWhiteSpace(run.Error))
                    Console.WriteLine($"Error: {run.Error}");
                if (!string.IsNullOrWhiteSpace(run.FinalText))
                    Console.WriteLine($"Final text: {run.FinalText}");

                if (verbose)
                {
                    foreach (var step in run.StepResults)
                    {
                        var line = $"- Step: {step.Id} | kind={step.Kind} | status={step.Status} | duration_ms={step.DurationMs:0.###}";
                        if (!string.IsNullOrWhiteSpace(step.FailureCode))
                            line += $" | failure_code={step.FailureCode}";
                        if (step.Continued)
                            line += " | continued=true";
                        Console.WriteLine(line);
                    }

                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine();
                }
            }

            return 0;
        }
        finally
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static async Task<int> PreviewMetaRunReplayAsync(string[] args)
    {
        var asJson = args.Contains("--json");
        var sessionId = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Console.Error.WriteLine("Usage: openclaw skills meta-runs replay <session-id> --run <run-id> [--storage <path>] [--json]");
            return 2;
        }

        var requestedRunId = GetOptionValue(args, "--run");
        if (string.IsNullOrWhiteSpace(requestedRunId))
        {
            Console.Error.WriteLine("--run <run-id> is required for meta-runs replay preview.");
            return 2;
        }

        var storagePath = GetOptionValue(args, "--storage");
        var store = OpenMemoryStore(storagePath);
        try
        {
            var session = await store.GetSessionAsync(sessionId, CancellationToken.None);
            if (session is null)
            {
                Console.Error.WriteLine($"Session '{sessionId}' not found.");
                return 1;
            }

            var run = session.MetaRunHistory.FirstOrDefault(run => string.Equals(run.RunId, requestedRunId, StringComparison.Ordinal));
            if (run is null)
            {
                Console.Error.WriteLine($"Run '{requestedRunId}' not found in session '{sessionId}'.");
                return 1;
            }

            var preview = BuildReplayPreview(sessionId, run);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(preview, CoreJsonContext.Default.MetaRunReplayPreviewResponse));
            }
            else
            {
                Console.WriteLine($"Replay preview for run: {preview.RunId}");
                Console.WriteLine($"Session: {preview.SessionId}");
                Console.WriteLine($"Skill: {preview.SkillName}");
                Console.WriteLine(preview.ReplayAvailable ? "Replay available: yes" : "Replay available: no");
                Console.WriteLine($"Reason: {preview.Reason}");
                Console.WriteLine("Replay plan:");
                Console.WriteLine($"Summary: {preview.Plan.Summary}");
                Console.WriteLine($"Mode: {preview.Plan.Mode}");
                Console.WriteLine(preview.Plan.Executable ? "Executable: yes" : "Executable: no");
                if (preview.Plan.ReplayableSteps.Length > 0)
                    Console.WriteLine($"Replayable steps: {string.Join(", ", preview.Plan.ReplayableSteps.Select(static item => $"{item.Id} | readiness={item.Readiness} | reason={item.Reason}"))}");
                if (preview.Plan.BlockedByRequirements.Length > 0)
                    Console.WriteLine($"Blocked by requirements: {string.Join(", ", preview.Plan.BlockedByRequirements.Select(FormatReplayRequirement))}");
                if (preview.AvailableArtifacts.Length > 0)
                {
                    Console.WriteLine("Available artifacts:");
                    foreach (var item in preview.AvailableArtifacts)
                        Console.WriteLine($"- {item}");
                }
                if (preview.RetainedSteps.Length > 0)
                {
                    Console.WriteLine("Retained steps:");
                    foreach (var step in preview.RetainedSteps)
                        Console.WriteLine(FormatReplayRetainedStep(step));
                }
                if (preview.MissingRequirements.Length > 0)
                {
                    Console.WriteLine("Missing replay inputs:");
                    foreach (var item in preview.MissingRequirements)
                        Console.WriteLine($"- {FormatReplayRequirement(item)}");
                }
            }

            return 1;
        }
        finally
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static Task<int> InspectAsync(string[] args)
    {
        var sourcePath = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Console.Error.WriteLine("Usage: openclaw skills inspect <path|tarball>");
            return Task.FromResult(2);
        }

        return InspectSourceAsync(sourcePath, printInstallTarget: false);
    }

    private static async Task<int> InstallAsync(string[] args)
    {
        var sourcePath = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Console.Error.WriteLine("Usage: openclaw skills install <path|tarball>");
            return 2;
        }

        var dryRun = args.Contains("--dry-run");
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");

        var resolved = await InspectResolvedSourceAsync(sourcePath, retainExtractedDirectory: true);
        var inspected = resolved.Inspection;
        if (!inspected.Success)
        {
            Console.Error.WriteLine(inspected.ErrorMessage);
            return 1;
        }

        try
        {
            PrintInspection(inspected, ResolveSkillsDirectory(managed, workdir));
            if (dryRun)
                return 0;

            var skillsDirectory = ResolveSkillsDirectory(managed, workdir);
            Directory.CreateDirectory(skillsDirectory);

            var targetDir = Path.Combine(skillsDirectory, inspected.InstallSlug);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);

            CopyDirectory(inspected.SkillRootPath, targetDir);
            Console.WriteLine($"Installed skill '{inspected.Definition.Name}' to {targetDir}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(resolved.TempDirectory))
            {
                try { Directory.Delete(resolved.TempDirectory, recursive: true); } catch { }
            }
        }
    }

    private static int ListInstalled(string[] args)
    {
        var managed = args.Contains("--managed");
        var workdir = GetOptionValue(args, "--workdir");
        var skillsDirectory = ResolveSkillsDirectory(managed, workdir);

        if (!Directory.Exists(skillsDirectory))
        {
            Console.WriteLine("No skills installed.");
            return 0;
        }

        var source = managed ? SkillSource.Managed : SkillSource.Workspace;
        var inspections = SkillInspector.InspectInstalledRoot(skillsDirectory, source)
            .Where(static inspection => inspection.Success && inspection.Definition is not null)
            .Select(CreateInspection)
            .OrderBy(static item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (inspections.Length == 0)
        {
            Console.WriteLine("No skills installed.");
            return 0;
        }

        Console.WriteLine($"Installed skills ({inspections.Length}):");
        foreach (var inspection in inspections)
        {
            Console.WriteLine($"  {inspection.Definition.Name} - {inspection.Definition.Description}");
            Console.WriteLine($"    Trust: {inspection.TrustLevel}");
            Console.WriteLine($"    Source: {inspection.SourceLabel}");
            Console.WriteLine($"    Path: {inspection.SkillRootPath}");
        }

        return 0;
    }

    private static async Task<int> InspectSourceAsync(string sourcePath, bool printInstallTarget)
    {
        var resolved = await InspectResolvedSourceAsync(sourcePath, retainExtractedDirectory: false);
        var inspected = resolved.Inspection;
        if (!inspected.Success)
        {
            Console.Error.WriteLine(inspected.ErrorMessage);
            return 1;
        }

        PrintInspection(inspected, printInstallTarget ? ResolveSkillsDirectory(managed: false, workdir: null) : null);
        return 0;
    }

    private static async Task<(SkillCommandInspection Inspection, string? TempDirectory)> InspectResolvedSourceAsync(string sourcePath, bool retainExtractedDirectory)
    {
        var resolvedSourcePath = Path.GetFullPath(sourcePath);

        if (Directory.Exists(resolvedSourcePath))
        {
            var inspection = SkillInspector.InspectPath(resolvedSourcePath, SkillSource.Extra);
            return inspection.Success
                ? (CreateInspection(inspection), null)
                : (SkillCommandInspection.Failure(inspection.ErrorMessage ?? $"Failed to inspect {resolvedSourcePath}."), null);
        }

        if (File.Exists(resolvedSourcePath) && resolvedSourcePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-install-{Guid.NewGuid():N}"[..24]);
            Directory.CreateDirectory(tempDir);

            try
            {
                var extractResult = await RunProcessAsync(
                    "tar",
                    ["--extract", "--gzip", "--file", resolvedSourcePath, "--directory", tempDir],
                    tempDir);
                if (extractResult.ExitCode != 0)
                    return (SkillCommandInspection.Failure($"Failed to extract skill tarball: {extractResult.Stderr}"), retainExtractedDirectory ? tempDir : null);

                var inspection = SkillInspector.InspectPath(tempDir, SkillSource.Extra);
                if (!inspection.Success)
                    return (SkillCommandInspection.Failure(inspection.ErrorMessage ?? $"Failed to inspect extracted tarball {resolvedSourcePath}."), retainExtractedDirectory ? tempDir : null);

                return (CreateInspection(inspection), retainExtractedDirectory ? tempDir : null);
            }
            finally
            {
                if (!retainExtractedDirectory)
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        return (SkillCommandInspection.Failure($"Skill path not found: {sourcePath}"), null);
    }

    private static SkillCommandInspection CreateInspection(SkillInspectionResult inspection)
    {
        var definition = inspection.Definition!;
        var installSlug = Slugify(definition.Metadata.SkillKey ?? definition.Name);
        var trustLevel = definition.Source == SkillSource.Bundled ? "first-party" : "upstream-compatible";
        var trustReason = definition.Source == SkillSource.Bundled
            ? "Skill ships with OpenClaw.NET."
            : "Skill document parsed successfully and uses the OpenClaw skill format.";

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Description))
            warnings.Add("Skill description is empty.");
        if (definition.Metadata.RequireBins.Length == 0 &&
            definition.Metadata.RequireAnyBins.Length == 0 &&
            definition.Metadata.RequireEnv.Length == 0 &&
            definition.Metadata.RequireConfig.Length == 0)
        {
            warnings.Add("Skill does not declare host requirements.");
        }

        return new SkillCommandInspection
        {
            Success = true,
            Definition = definition,
            SkillRootPath = inspection.SkillRootPath!,
            SkillFilePath = inspection.SkillFilePath!,
            InstallSlug = installSlug,
            SourceLabel = definition.Source.ToString().ToLowerInvariant(),
            TrustLevel = trustLevel,
            TrustReason = trustReason,
            Warnings = warnings
        };
    }

    private static void PrintInspection(SkillCommandInspection inspection, string? installDirectory)
    {
        Console.WriteLine($"Skill: {inspection.Definition.Name}");
        Console.WriteLine($"Description: {inspection.Definition.Description}");
        Console.WriteLine($"Trust: {inspection.TrustLevel}");
        Console.WriteLine($"Trust reason: {inspection.TrustReason}");
        Console.WriteLine($"Source: {inspection.SourceLabel}");
        Console.WriteLine($"Path: {inspection.SkillRootPath}");
        Console.WriteLine($"Install slug: {inspection.InstallSlug}");
        Console.WriteLine($"User invocable: {inspection.Definition.UserInvocable}");
        Console.WriteLine($"Disable model invocation: {inspection.Definition.DisableModelInvocation}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandDispatch))
            Console.WriteLine($"Command dispatch: {inspection.Definition.CommandDispatch}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandTool))
            Console.WriteLine($"Command tool: {inspection.Definition.CommandTool}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.CommandArgMode))
            Console.WriteLine($"Command arg mode: {inspection.Definition.CommandArgMode}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.Metadata.Homepage))
            Console.WriteLine($"Homepage: {inspection.Definition.Metadata.Homepage}");
        if (!string.IsNullOrWhiteSpace(inspection.Definition.Metadata.PrimaryEnv))
            Console.WriteLine($"Primary env: {inspection.Definition.Metadata.PrimaryEnv}");
        Console.WriteLine($"Requirements: {BuildRequirementsSummary(inspection.Definition)}");
        foreach (var warning in inspection.Warnings)
            Console.WriteLine($"Warning: {warning}");
        if (!string.IsNullOrWhiteSpace(installDirectory))
            Console.WriteLine($"Install target: {Path.Combine(installDirectory, inspection.InstallSlug)}");
    }

    private static string BuildRequirementsSummary(SkillDefinition definition)
    {
        var items = new List<string>();
        if (definition.Metadata.RequireBins.Length > 0)
            items.Add($"bins={string.Join(",", definition.Metadata.RequireBins)}");
        if (definition.Metadata.RequireAnyBins.Length > 0)
            items.Add($"anyBins={string.Join(",", definition.Metadata.RequireAnyBins)}");
        if (definition.Metadata.RequireEnv.Length > 0)
            items.Add($"env={string.Join(",", definition.Metadata.RequireEnv)}");
        if (definition.Metadata.RequireConfig.Length > 0)
            items.Add($"config={string.Join(",", definition.Metadata.RequireConfig)}");
        if (definition.Metadata.Always)
            items.Add("always");

        return items.Count == 0 ? "none" : string.Join(" | ", items);
    }

    private static string ResolveSkillsDirectory(bool managed, string? workdir)
    {
        if (!string.IsNullOrWhiteSpace(workdir))
            return Path.Combine(Path.GetFullPath(workdir), "skills");

        if (managed)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".openclaw", "skills");
        }

        var workspace = Environment.GetEnvironmentVariable(EnvWorkspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(Path.GetFullPath(workspace), "skills");

        throw new InvalidOperationException($"Missing {EnvWorkspace}. Set {EnvWorkspace} or pass --workdir or --managed.");
    }

    private static string? GetOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static int? ParseIntOption(string[] args, string optionName)
    {
        var value = GetOptionValue(args, optionName);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static IMemoryStore OpenMemoryStore(string? storagePath)
    {
        var resolved = ResolveMemoryPath(storagePath);
        if (LooksLikeSqlitePath(resolved))
            return new SqliteMemoryStore(resolved, enableFts: false);

        return new FileMemoryStore(resolved);
    }

    private static string ResolveMemoryPath(string? storagePath)
    {
        if (!string.IsNullOrWhiteSpace(storagePath))
            return Path.GetFullPath(storagePath);

        var workspace = Environment.GetEnvironmentVariable(EnvWorkspace);
        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(Path.GetFullPath(workspace), "memory");

        return Path.GetFullPath("./memory");
    }

    private static bool LooksLikeSqlitePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        ThrowIfReparsePoint(new DirectoryInfo(source));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            ThrowIfReparsePoint(new FileInfo(file));
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            ThrowIfReparsePoint(new DirectoryInfo(dir));
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void ThrowIfReparsePoint(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to install skill package containing a symlink or reparse point: {info.FullName}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return (127, "", $"Command not found: {fileName}");
        }
    }

    private static string Slugify(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int UnknownSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown skills subcommand: {subcommand}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            openclaw skills — Inspect and install local OpenClaw skill packages

            Usage:
              openclaw skills inspect <path|tarball>
              openclaw skills install <path|tarball> [--dry-run] [--workdir <path> | --managed]
              openclaw skills list [--workdir <path> | --managed]
                            openclaw skills meta-runs <session-id> [--storage <path>] [--limit <count>] [--run <run-id>] [--verbose] [--json]
                            openclaw skills meta-runs replay <session-id> --run <run-id> [--storage <path>] [--json]

            Notes:
              - Remote registry installs still go through `openclaw clawhub`.
              - `install --dry-run` prints trust and requirement details without copying files.
                            - `meta-runs` reads persisted local session state from `./memory`, `$OPENCLAW_WORKSPACE/memory`, or `--storage`.
                            - `meta-runs --run <run-id>` limits output to one persisted run inside the session history.
                            - `meta-runs --verbose` expands per-step trace summaries for each persisted run.
                            - `meta-runs --json` emits machine-readable output for operators and scripts.
                            - `meta-runs replay` is currently preview-only and reports whether persisted run history is sufficient for replay.
            """);
    }

        private static void WriteMetaRunsJson(string sessionId, int totalCount, IReadOnlyList<SessionMetaRunRecord> runs)
        {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                        writer.WriteStartObject();
                        writer.WriteString("sessionId", sessionId);
                        writer.WriteNumber("totalCount", totalCount);
                        writer.WriteNumber("shownCount", runs.Count);
                        writer.WritePropertyName("runs");
                        writer.WriteStartArray();
                        foreach (var run in runs)
                                JsonSerializer.Serialize(writer, run, CoreJsonContext.Default.SessionMetaRunRecord);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                }

                Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }

    private static MetaRunReplayPreviewResponse BuildReplayPreview(string sessionId, SessionMetaRunRecord run)
    {
        var missingRequirements = GetReplayMissingRequirements(run);
        return new MetaRunReplayPreviewResponse
        {
            SessionId = sessionId,
            RunId = run.RunId,
            SkillName = run.SkillName,
            ReplayAvailable = false,
            Reason = MetaRunReplayReasons.NotEnoughInputsForExecutableReplay,
            AvailableArtifacts = GetReplayAvailableArtifacts(run),
            RetainedSteps = GetReplayRetainedSteps(run),
            Plan = GetReplayPlan(run, missingRequirements),
            MissingRequirements = missingRequirements
        };
    }

    private static string FormatReplayRequirement(MetaRunReplayRequirementPreview requirement)
        => $"{requirement.Name} | kind={requirement.Kind} | reason={requirement.Reason}";

    private static string FormatReplayRetainedStep(MetaRunReplayStepPreview step)
    {
        var line = $"- {step.Id} | kind={step.Kind} | status={step.Status} | duration_ms={step.DurationMs:0.###}";
        if (!string.IsNullOrWhiteSpace(step.FailureCode))
            line += $" | failure_code={step.FailureCode}";
        if (step.Continued)
            line += " | continued=true";

        return line;
    }

    private static string[] GetReplayAvailableArtifacts(SessionMetaRunRecord run)
    {
        var artifacts = new List<string>();
        if (!string.IsNullOrWhiteSpace(run.FinalText))
            artifacts.Add(MetaRunReplayArtifactNames.FinalText);
        if (!string.IsNullOrWhiteSpace(run.ErrorCode))
            artifacts.Add(MetaRunReplayArtifactNames.ErrorCode);
        if (!string.IsNullOrWhiteSpace(run.Error))
            artifacts.Add(MetaRunReplayArtifactNames.ErrorMessage);
        if (HasRetainedSteps(run))
            artifacts.Add(MetaRunReplayArtifactNames.StepResults);

        return [.. artifacts];
    }

    private static MetaRunReplayRequirementPreview[] GetReplayMissingRequirements(SessionMetaRunRecord run)
    {
        var requirements = new List<MetaRunReplayRequirementPreview>
        {
            new()
            {
                Name = MetaRunReplayRequirementNames.PromptContext,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.PromptContextNotPersisted
            },
            new()
            {
                Name = MetaRunReplayRequirementNames.StepInputs,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.StepInputsNotPersisted
            },
            new()
            {
                Name = MetaRunReplayRequirementNames.ToolArguments,
                Kind = MetaRunReplayRequirementKinds.NotPersisted,
                Reason = MetaRunReplayRequirementReasons.ToolArgumentsNotPersisted
            }
        };

        if (!HasRetainedSteps(run))
        {
            requirements.Add(new MetaRunReplayRequirementPreview
            {
                Name = MetaRunReplayRequirementNames.StepResults,
                Kind = MetaRunReplayRequirementKinds.NotRetained,
                Reason = MetaRunReplayRequirementReasons.StepResultsNotRetained
            });
        }

        return [.. requirements];
    }

    private static MetaRunReplayStepPreview[] GetReplayRetainedSteps(SessionMetaRunRecord run)
        => [.. run.StepResults.Select(static step => new MetaRunReplayStepPreview
        {
            Id = step.Id,
            Kind = step.Kind,
            Status = step.Status,
            FailureCode = step.FailureCode,
            DurationMs = step.DurationMs,
            Continued = step.Continued
        })];

    private static MetaRunReplayPlanPreview GetReplayPlan(SessionMetaRunRecord run, MetaRunReplayRequirementPreview[] missingRequirements)
    {
        return new MetaRunReplayPlanPreview
        {
            Summary = GetReplayPlanSummary(run),
            Mode = MetaRunReplayModes.PreviewOnly,
            Executable = false,
            ReplayableSteps = [.. run.StepResults.Select(GetReplayStepReadiness)],
            BlockedByRequirements = missingRequirements
        };
    }

    private static MetaRunReplayStepReadinessPreview GetReplayStepReadiness(SessionMetaStepResult step)
    {
        if ((string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(step.FailureCode))
            && step.Continued)
        {
            return new MetaRunReplayStepReadinessPreview
            {
                Id = step.Id,
                Readiness = MetaRunReplayStepReadinessKinds.FailureTraceContinued,
                Reason = MetaRunReplayStepReadinessReasons.FailureTraceContinued
            };
        }

        if (string.Equals(step.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(step.FailureCode))
        {
            return new MetaRunReplayStepReadinessPreview
            {
                Id = step.Id,
                Readiness = MetaRunReplayStepReadinessKinds.FailureTraceOnly,
                Reason = MetaRunReplayStepReadinessReasons.FailureTraceOnly
            };
        }

        if (step.Continued)
        {
            return new MetaRunReplayStepReadinessPreview
            {
                Id = step.Id,
                Readiness = MetaRunReplayStepReadinessKinds.ContinuationTraceOnly,
                Reason = MetaRunReplayStepReadinessReasons.ContinuationTraceOnly
            };
        }

        return new MetaRunReplayStepReadinessPreview
        {
            Id = step.Id,
            Readiness = MetaRunReplayStepReadinessKinds.TraceOnly,
            Reason = MetaRunReplayStepReadinessReasons.TraceOnly
        };
    }

    private static string GetReplayPlanSummary(SessionMetaRunRecord run)
        => HasRetainedSteps(run)
            ? MetaRunReplayPlanSummaries.AuditableNotReplayable
            : MetaRunReplayPlanSummaries.MetadataOnlyNotReplayable;

    private static bool HasRetainedSteps(SessionMetaRunRecord run)
        => run.StepResults.Count > 0;

    internal sealed class SkillCommandInspection
    {
        public required bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public required SkillDefinition Definition { get; init; }
        public required string SkillRootPath { get; init; }
        public required string SkillFilePath { get; init; }
        public required string InstallSlug { get; init; }
        public required string SourceLabel { get; init; }
        public required string TrustLevel { get; init; }
        public required string TrustReason { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];

        public static SkillCommandInspection Failure(string errorMessage)
            => new()
            {
                Success = false,
                ErrorMessage = errorMessage,
                Definition = new SkillDefinition
                {
                    Name = string.Empty,
                    Description = string.Empty,
                    Instructions = string.Empty,
                    Location = string.Empty,
                    Source = SkillSource.Extra
                },
                SkillRootPath = string.Empty,
                SkillFilePath = string.Empty,
                InstallSlug = string.Empty,
                SourceLabel = string.Empty,
                TrustLevel = "untrusted",
                TrustReason = string.Empty
            };
    }
}
