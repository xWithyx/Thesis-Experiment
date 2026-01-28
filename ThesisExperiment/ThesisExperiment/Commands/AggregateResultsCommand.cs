using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ThesisExperiment.Models;

namespace ThesisExperiment.Commands
{
    /// <summary>Aggregates RunRecord JSONs into flat CSV and summary files.</summary>
    public class AggregateResultsCommand
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public async Task ExecuteAsync(string runsDir, string outputDir)
        {
            Console.WriteLine("=== Aggregate Results ===");
            Console.WriteLine($"  Runs directory : {Path.GetFullPath(runsDir)}");
            Console.WriteLine($"  Output directory: {Path.GetFullPath(outputDir)}");

            // --- Load all RunRecord JSONs ---
            var (records, parseOk, parseFailed) = LoadRunRecords(runsDir);
            Console.WriteLine($"  Total JSON files found: {parseOk + parseFailed}");
            Console.WriteLine($"  Successfully parsed   : {parseOk}");
            Console.WriteLine($"  Failed to parse       : {parseFailed}");

            if (records.Count == 0)
            {
                Console.WriteLine("  No records to aggregate. Exiting.");
                return;
            }

            // --- Block 1: Flatten to CSV ---
            var aggregatesDir = Path.Combine(outputDir, "aggregates");
            Directory.CreateDirectory(aggregatesDir);

            var flatRows = records.Select(MapToFlatRow).ToList();
            var flatCsvPath = Path.Combine(aggregatesDir, "runs_flat.csv");
            WriteCsv(flatCsvPath, flatRows);
            Console.WriteLine($"  Written: {flatCsvPath} ({flatRows.Count} rows)");

            // --- Block 2: Summaries ---
            WriteSummaryByVariant(aggregatesDir, flatRows);
            WriteSummaryByStatus(aggregatesDir, flatRows);
            WriteSummaryByProject(aggregatesDir, flatRows);
            WriteMarkdownReport(aggregatesDir, flatRows, parseOk, parseFailed);

            Console.WriteLine("=== Aggregation complete ===");
            await Task.CompletedTask;
        }

        // ───────────────────────── Block 1: Load & Flatten ─────────────────────────

        private (List<RunRecord> Records, int ParseOk, int ParseFailed) LoadRunRecords(string runsDir)
        {
            var records = new List<RunRecord>();
            int ok = 0, failed = 0;

            if (!Directory.Exists(runsDir))
            {
                Console.WriteLine($"  WARNING: Runs directory does not exist: {runsDir}");
                return (records, ok, failed);
            }

            var jsonFiles = Directory.GetFiles(runsDir, "*.json", SearchOption.AllDirectories);

            foreach (var file in jsonFiles)
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var record = JsonSerializer.Deserialize<RunRecord>(json, JsonOptions);
                    if (record != null)
                    {
                        records.Add(record);
                        ok++;
                    }
                    else
                    {
                        Console.WriteLine($"  WARNING: Null after deserializing {file}");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Failed to parse {Path.GetFileName(file)}: {ex.Message}");
                    failed++;
                }
            }

            return (records, ok, failed);
        }

        private static RunFlatRow MapToFlatRow(RunRecord r)
        {
            return new RunFlatRow
            {
                // Experiment
                Variant = r.Experiment.Variant,
                AttemptNumber = r.Experiment.AttemptNumber,
                MaxAttempts = r.Experiment.MaxAttempts,

                // Project
                ProjectName = r.Project.Name,
                RepoUrl = r.Project.RepoUrl,
                Stars = r.Project.Stars,
                CommitHash = r.Project.CommitHash,

                // Method
                MethodIdentifier = r.Method.Identifier,
                ContainingClass = r.Method.ContainingClass,
                MethodSignature = r.Method.Signature,
                FilePath = r.Method.FilePath,
                LineStart = r.Method.LineStart,
                LineEnd = r.Method.LineEnd,

                // Outcome
                FinalStatus = r.Outcome.FinalStatus,
                StopReason = r.Outcome.StopReason,
                Gate1BuildPassed = r.Outcome.Gate1BuildPassed,
                Gate2TestPassed = r.Outcome.Gate2TestPassed,

                // Build & Test
                BuildExitCode = r.Build.ExitCode,
                TestExitCode = r.Test.ExitCode,

                // Flakiness
                FlakinessClassification = r.Flakiness.Classification,
                FlakinessRunsExecuted = r.Flakiness.RunsExecuted,
                FlakinessRunResults = string.Join("|", r.Flakiness.RunResults ?? new List<string>()),

                // Coverage
                CoverageStatus = r.Coverage.Status,
                CoverageLinePercent = r.Coverage.LinePercent,
                CoverageBranchPercent = r.Coverage.BranchPercent,

                // Mutation
                MutationScore = r.Mutation.MutationScore,
                MutantsKilled = r.Mutation.MutantsKilled,
                MutantsSurvived = r.Mutation.MutantsSurvived,
                MutantsTotal = r.Mutation.MutantsTotal,

                // Model
                ModelProvider = r.Model.Provider,
                ModelId = r.Model.ModelId,
                Temperature = r.Model.Temperature,
                MaxTokens = r.Model.MaxTokens,
                RequestId = r.Model.RequestId,

                // Tokens
                TokensUsed = r.Response.TokensUsed,
                PromptTokens = r.Response.PromptTokens,
                CompletionTokens = r.Response.CompletionTokens,
                FinishReason = r.Response.FinishReason,

                // Prompt metadata
                PromptVersion = r.Experiment.PromptVersion,
                PromptFile = r.Experiment.PromptFile,
                TemplateVersion = r.Prompt.TemplateVersion,
                TemplateFile = r.Prompt.TemplateFile,

                // Error
                ErrorCategory = r.Error.Category,
                ErrorSubcategory = r.Error.Subcategory,

                // Timestamps
                TimestampStart = r.TimestampStart,
                TimestampEnd = r.TimestampEnd,
                RunId = r.RunId
            };
        }

        private static void WriteCsv<T>(string filePath, List<T> rows)
        {
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csv.WriteRecords(rows);
        }

        // ───────────────────────── Block 2: Summaries ─────────────────────────

        private void WriteSummaryByVariant(string aggregatesDir, List<RunFlatRow> rows)
        {
            var variants = rows
                .GroupBy(r => r.Variant)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var total = g.Count();
                    var completed = g.Where(r => r.FinalStatus == "completed").ToList();

                    return new
                    {
                        Variant = g.Key,
                        TotalRuns = total,
                        CompletedCount = completed.Count,
                        BuildFailedCount = g.Count(r => r.FinalStatus == "build_failed"),
                        TestFailedCount = g.Count(r => r.FinalStatus == "test_failed"),
                        FlakyFailureCount = g.Count(r => r.FinalStatus == "flaky_failure"),
                        ApiErrorCount = g.Count(r => r.FinalStatus == "api_error"),
                        LlmInvalidOutputCount = g.Count(r => r.FinalStatus == "llm_invalid_output"),
                        CloneFailedCount = g.Count(r => r.FinalStatus == "clone_failed"),
                        NoTestProjectCount = g.Count(r => r.FinalStatus == "no_test_project"),
                        BuildPassRate = total > 0
                            ? Math.Round(100.0 * g.Count(r => r.Gate1BuildPassed) / total, 2)
                            : 0.0,
                        TestPassRate = total > 0
                            ? Math.Round(100.0 * g.Count(r => r.Gate2TestPassed) / total, 2)
                            : 0.0,
                        StablePassRate = total > 0
                            ? Math.Round(100.0 * completed.Count / total, 2)
                            : 0.0,
                        AvgCoverageLinePercent = completed.Count > 0
                            ? Math.Round(completed.Where(c => c.CoverageLinePercent.HasValue)
                                .Select(c => c.CoverageLinePercent!.Value).DefaultIfEmpty(0).Average(), 2)
                            : (double?)null,
                        AvgCoverageBranchPercent = completed.Count > 0
                            ? Math.Round(completed.Where(c => c.CoverageBranchPercent.HasValue)
                                .Select(c => c.CoverageBranchPercent!.Value).DefaultIfEmpty(0).Average(), 2)
                            : (double?)null,
                        AvgMutationScore = completed.Count > 0
                            ? Math.Round(completed.Where(c => c.MutationScore.HasValue)
                                .Select(c => c.MutationScore!.Value).DefaultIfEmpty(0).Average(), 2)
                            : (double?)null,
                        AvgTokensUsed = total > 0
                            ? Math.Round((double)g.Sum(r => r.TokensUsed) / total, 0)
                            : 0.0,
                        SumTokensUsed = g.Sum(r => r.TokensUsed)
                    };
                })
                .ToList();

            var path = Path.Combine(aggregatesDir, "summary_by_variant.csv");
            WriteCsv(path, variants);
            Console.WriteLine($"  Written: {path}");
        }

        private void WriteSummaryByStatus(string aggregatesDir, List<RunFlatRow> rows)
        {
            var statuses = rows
                .GroupBy(r => new { r.Variant, r.FinalStatus })
                .OrderBy(g => g.Key.Variant)
                .ThenBy(g => g.Key.FinalStatus)
                .Select(g => new
                {
                    g.Key.Variant,
                    g.Key.FinalStatus,
                    Count = g.Count()
                })
                .ToList();

            var path = Path.Combine(aggregatesDir, "summary_by_status.csv");
            WriteCsv(path, statuses);
            Console.WriteLine($"  Written: {path}");
        }

        private void WriteSummaryByProject(string aggregatesDir, List<RunFlatRow> rows)
        {
            var projects = rows
                .GroupBy(r => new { r.ProjectName, r.Variant })
                .OrderBy(g => g.Key.ProjectName)
                .ThenBy(g => g.Key.Variant)
                .Select(g =>
                {
                    var completed = g.Where(r => r.FinalStatus == "completed").ToList();

                    return new
                    {
                        g.Key.ProjectName,
                        g.Key.Variant,
                        TotalRuns = g.Count(),
                        Completed = completed.Count,
                        BuildFailed = g.Count(r => r.FinalStatus == "build_failed"),
                        TestFailed = g.Count(r => r.FinalStatus == "test_failed"),
                        FlakyFailure = g.Count(r => r.FinalStatus == "flaky_failure"),
                        AvgCoverageLinePercent = completed.Count > 0
                            ? Math.Round(completed.Where(c => c.CoverageLinePercent.HasValue)
                                .Select(c => c.CoverageLinePercent!.Value).DefaultIfEmpty(0).Average(), 2)
                            : (double?)null,
                        AvgMutationScore = completed.Count > 0
                            ? Math.Round(completed.Where(c => c.MutationScore.HasValue)
                                .Select(c => c.MutationScore!.Value).DefaultIfEmpty(0).Average(), 2)
                            : (double?)null,
                        AvgTokensUsed = g.Count() > 0
                            ? Math.Round((double)g.Sum(r => r.TokensUsed) / g.Count(), 0)
                            : 0.0
                    };
                })
                .ToList();

            var path = Path.Combine(aggregatesDir, "summary_by_project.csv");
            WriteCsv(path, projects);
            Console.WriteLine($"  Written: {path}");
        }

        private void WriteMarkdownReport(string aggregatesDir, List<RunFlatRow> rows,
            int parseOk, int parseFailed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Aggregate Report");
            sb.AppendLine();
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();

            // Files
            sb.AppendLine("## Files Generated");
            sb.AppendLine();
            sb.AppendLine("- `runs_flat.csv` — one row per RunRecord");
            sb.AppendLine("- `summary_by_variant.csv` — metrics per variant (A, B, C)");
            sb.AppendLine("- `summary_by_status.csv` — counts per FinalStatus per variant");
            sb.AppendLine("- `summary_by_project.csv` — metrics per project per variant");
            sb.AppendLine();

            // Overview
            sb.AppendLine("## Overview");
            sb.AppendLine();
            sb.AppendLine($"- Total JSON files parsed: **{parseOk}** (failed: {parseFailed})");
            sb.AppendLine($"- Total runs: **{rows.Count}**");
            var variantCounts = rows.GroupBy(r => r.Variant).OrderBy(g => g.Key);
            foreach (var vc in variantCounts)
            {
                sb.AppendLine($"  - Variant {vc.Key}: {vc.Count()} runs");
            }
            sb.AppendLine();

            // Pass Rates per Variant
            sb.AppendLine("## Pass Rates by Variant");
            sb.AppendLine();
            sb.AppendLine("| Variant | Total | Completed | Build Pass % | Test Pass % | Stable Pass % | Flaky |");
            sb.AppendLine("|---------|-------|-----------|-------------|-------------|---------------|-------|");
            foreach (var g in rows.GroupBy(r => r.Variant).OrderBy(g => g.Key))
            {
                var total = g.Count();
                var completed = g.Count(r => r.FinalStatus == "completed");
                var buildPass = total > 0 ? Math.Round(100.0 * g.Count(r => r.Gate1BuildPassed) / total, 1) : 0;
                var testPass = total > 0 ? Math.Round(100.0 * g.Count(r => r.Gate2TestPassed) / total, 1) : 0;
                var stablePass = total > 0 ? Math.Round(100.0 * completed / total, 1) : 0;
                var flaky = g.Count(r => r.FinalStatus == "flaky_failure");
                sb.AppendLine($"| {g.Key} | {total} | {completed} | {buildPass}% | {testPass}% | {stablePass}% | {flaky} |");
            }
            sb.AppendLine();

            // Coverage & Mutation (completed only)
            var completedRows = rows.Where(r => r.FinalStatus == "completed").ToList();
            sb.AppendLine("## Coverage & Mutation (completed runs only)");
            sb.AppendLine();
            if (completedRows.Count > 0)
            {
                sb.AppendLine("| Variant | N | Avg Line Cov % | Avg Branch Cov % | Avg Mutation Score % |");
                sb.AppendLine("|---------|---|---------------|-----------------|---------------------|");
                foreach (var g in completedRows.GroupBy(r => r.Variant).OrderBy(g => g.Key))
                {
                    var n = g.Count();
                    var avgLine = g.Where(c => c.CoverageLinePercent.HasValue)
                        .Select(c => c.CoverageLinePercent!.Value).DefaultIfEmpty(0).Average();
                    var avgBranch = g.Where(c => c.CoverageBranchPercent.HasValue)
                        .Select(c => c.CoverageBranchPercent!.Value).DefaultIfEmpty(0).Average();
                    var avgMutation = g.Where(c => c.MutationScore.HasValue)
                        .Select(c => c.MutationScore!.Value).DefaultIfEmpty(0).Average();
                    sb.AppendLine($"| {g.Key} | {n} | {avgLine:F1}% | {avgBranch:F1}% | {avgMutation:F1}% |");
                }
            }
            else
            {
                sb.AppendLine("_No completed runs found._");
            }
            sb.AppendLine();

            // Token Usage
            sb.AppendLine("## Token Usage");
            sb.AppendLine();
            sb.AppendLine("| Variant | Total Tokens | Avg Tokens/Run | Total Prompt | Total Completion |");
            sb.AppendLine("|---------|-------------|---------------|-------------|-----------------|");
            foreach (var g in rows.GroupBy(r => r.Variant).OrderBy(g => g.Key))
            {
                var total = g.Count();
                var sumTokens = g.Sum(r => r.TokensUsed);
                var avgTokens = total > 0 ? Math.Round((double)sumTokens / total, 0) : 0;
                var sumPrompt = g.Sum(r => r.PromptTokens);
                var sumCompletion = g.Sum(r => r.CompletionTokens);
                sb.AppendLine($"| {g.Key} | {sumTokens:N0} | {avgTokens:N0} | {sumPrompt:N0} | {sumCompletion:N0} |");
            }
            sb.AppendLine();

            // Status Breakdown
            sb.AppendLine("## Status Breakdown");
            sb.AppendLine();
            sb.AppendLine("| Variant | Status | Count |");
            sb.AppendLine("|---------|--------|-------|");
            foreach (var g in rows.GroupBy(r => new { r.Variant, r.FinalStatus })
                .OrderBy(g => g.Key.Variant).ThenBy(g => g.Key.FinalStatus))
            {
                sb.AppendLine($"| {g.Key.Variant} | {g.Key.FinalStatus} | {g.Count()} |");
            }
            sb.AppendLine();

            // Notes
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine("- Coverage and mutation metrics are only computed for `completed` runs (stable pass).");
            sb.AppendLine("- Variant A = existing tests (baseline), B = single-shot LLM, C = repair-loop LLM.");
            sb.AppendLine("- Flaky runs passed Gate 1+2 but had inconsistent results across 3 stability runs.");

            var path = Path.Combine(aggregatesDir, "aggregate_report.md");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"  Written: {path}");
        }
    }
}
