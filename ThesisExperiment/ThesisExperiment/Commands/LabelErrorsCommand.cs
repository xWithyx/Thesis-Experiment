using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using ThesisExperiment.Models;

namespace ThesisExperiment.Commands
{
    /// <summary>
    /// Labels RunRecord JSONs with standardised error categories and subcategories.
    /// Rules Version: v1.0
    /// </summary>
    public class LabelErrorsCommand
    {
        private const string LabelVersionId = "v1.0";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        // ── Regex patterns (compiled once) ──────────────────────────────────────

        private static readonly Regex CsErrorCodeRegex =
            new(@"\bCS(\d{4})\b", RegexOptions.Compiled);

        private static readonly Regex XunitAssertionRegex =
            new(@"Xunit\.Sdk\.", RegexOptions.Compiled);

        private static readonly Regex NunitAssertionRegex =
            new(@"NUnit\.Framework\.Assert(?:ion)?Exception", RegexOptions.Compiled);

        private static readonly Regex MsTestAssertionRegex =
            new(@"Microsoft\.VisualStudio\.TestTools\.UnitTesting\.AssertFailedException",
                RegexOptions.Compiled);

        private static readonly Regex NullRefRegex =
            new(@"System\.NullReferenceException", RegexOptions.Compiled);

        private static readonly Regex ArgNullRegex =
            new(@"System\.ArgumentNullException", RegexOptions.Compiled);

        private static readonly Regex ArgRegex =
            new(@"System\.ArgumentException", RegexOptions.Compiled);

        private static readonly Regex InvalidOpRegex =
            new(@"System\.InvalidOperationException", RegexOptions.Compiled);

        private static readonly Regex NoTestsRegex =
            new(@"No test (matches the given testcase filter|is available|source found)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FilterMismatchRegex =
            new(@"No test matches the given testcase filter",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CodeFenceRegex =
            new(@"```(?:csharp|cs)?", RegexOptions.Compiled);

        // ── Entry point ─────────────────────────────────────────────────────────

        public async Task ExecuteAsync(string runsDir, string outputDir)
        {
            Console.WriteLine("=== Label Errors ===");
            Console.WriteLine($"  Runs directory : {Path.GetFullPath(runsDir)}");
            Console.WriteLine($"  Output directory: {Path.GetFullPath(outputDir)}");
            Console.WriteLine($"  Rules version  : {LabelVersionId}");

            // 1. Load all RunRecord JSONs
            var (records, parseOk, parseFailed) = LoadRunRecords(runsDir);
            Console.WriteLine($"  Total JSON files found: {parseOk + parseFailed}");
            Console.WriteLine($"  Successfully parsed   : {parseOk}");
            Console.WriteLine($"  Failed to parse       : {parseFailed}");

            if (records.Count == 0)
            {
                Console.WriteLine("  No records to label. Exiting.");
                return;
            }

            // 2. Apply labels to each record
            var labeledRows = new List<RunFlatRow>();
            int labeledCount = 0;
            foreach (var record in records)
            {
                var (category, subcategory, primaryCode) = ApplyLabels(record);
                var row = MapToFlatRow(record, category, subcategory, primaryCode);
                labeledRows.Add(row);
                if (!string.IsNullOrEmpty(category))
                    labeledCount++;
            }

            Console.WriteLine($"  Records labeled: {labeledCount} / {records.Count}");

            // 3. Write outputs
            var labelsDir = Path.Combine(outputDir, "labels");
            Directory.CreateDirectory(labelsDir);

            // 3a. Flat labeled CSV
            var flatPath = Path.Combine(labelsDir, "runs_flat_labeled.csv");
            WriteCsv(flatPath, labeledRows);
            Console.WriteLine($"  Written: {flatPath} ({labeledRows.Count} rows)");

            // 3b. Summary by variant
            WriteSummaryByVariant(labelsDir, labeledRows);

            // 3c. Summary by status
            WriteSummaryByStatus(labelsDir, labeledRows);

            // 3d. Markdown report
            WriteMarkdownReport(labelsDir, labeledRows, parseOk, parseFailed);

            Console.WriteLine("=== Labeling complete ===");
            await Task.CompletedTask;
        }

        // ── Label logic ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deterministic labeling: first by FinalStatus, then deeper regex analysis.
        /// Returns (category, subcategory, primaryErrorCode).
        /// </summary>
        private (string Category, string Subcategory, string PrimaryCode) ApplyLabels(RunRecord record)
        {
            var status = record.Outcome.FinalStatus;

            // completed runs get no error label
            if (status == "completed")
                return (string.Empty, string.Empty, string.Empty);

            // Step 1: Route by FinalStatus
            return status switch
            {
                "clone_failed" => ("git", "clone_failed", string.Empty),
                "no_test_project" => ("discovery", "no_test_project", string.Empty),
                "api_error" => LabelApiError(record),
                "llm_invalid_output" => LabelLlmOutput(record),
                "build_failed" => LabelBuildError(record),
                "test_failed" => LabelTestError(record),
                "flaky_failure" => ("flakiness", "flaky_inconsistent_runs", string.Empty),
                _ => ("internal", $"unknown_status_{status}", string.Empty)
            };
        }

        private (string, string, string) LabelApiError(RunRecord record)
        {
            var text = CombineTexts(
                record.Error.RawMessage,
                record.Response.RawText);

            if (ContainsAny(text, "OPENAI_API_KEY", "not set", "API key"))
                return ("openai", "missing_api_key", string.Empty);

            if (ContainsAny(text, "401", "Unauthorized"))
                return ("openai", "auth_error", string.Empty);

            if (ContainsAny(text, "429", "rate limit", "Rate limit"))
                return ("openai", "rate_limited", string.Empty);

            if (ContainsAny(text, "timeout", "Timeout", "timed out"))
                return ("openai", "api_timeout", string.Empty);

            return ("openai", "other_api_error", string.Empty);
        }

        private (string, string, string) LabelLlmOutput(RunRecord record)
        {
            var rawText = record.Response.RawText ?? string.Empty;
            var extractedCode = record.Response.ExtractedCode ?? string.Empty;

            if (string.IsNullOrWhiteSpace(rawText) && string.IsNullOrWhiteSpace(extractedCode))
                return ("llm_output", "empty_code", string.Empty);

            if (!CodeFenceRegex.IsMatch(rawText) && string.IsNullOrWhiteSpace(extractedCode))
                return ("llm_output", "no_code_fence", string.Empty);

            if (string.IsNullOrWhiteSpace(extractedCode))
                return ("llm_output", "empty_code", string.Empty);

            // Check for multiple test classes
            var classMatches = Regex.Matches(extractedCode, @"\bclass\s+\w+");
            if (classMatches.Count > 1)
                return ("llm_output", "contains_multiple_tests", string.Empty);

            // Check for missing test attributes
            if (!ContainsAny(extractedCode, "[Fact]", "[Test]", "[TestMethod]",
                    "[Theory]", "[TestCase]", "[DataTestMethod]"))
                return ("llm_output", "missing_test_attribute", string.Empty);

            return ("llm_output", "no_code_fence", string.Empty);
        }

        private (string, string, string) LabelBuildError(RunRecord record)
        {
            var text = CombineTexts(
                record.Error.RawMessage,
                record.Build.Stderr,
                record.Build.Stdout);

            // Try to find CS error codes
            var csMatch = CsErrorCodeRegex.Match(text);
            if (csMatch.Success)
            {
                var code = csMatch.Value; // e.g. CS0122
                var codeNum = csMatch.Groups[1].Value;

                var subcategory = codeNum switch
                {
                    "0122" => "inaccessible_member",
                    "0246" => "missing_type_or_namespace",
                    "0103" => "unknown_identifier",
                    "0234" => "missing_reference",
                    "0029" => "implicit_conversion_error",
                    "1061" => "duplicate_member",
                    "0111" => "member_not_static",
                    "0116" => "no_instance_required",
                    "0117" => "member_not_found",
                    "0266" => "implicit_conversion_error",
                    "8370" => "file_scoped_namespace_conflict",
                    _ => $"compile_error_{code.ToLowerInvariant()}"
                };

                return ("build", subcategory, code);
            }

            // Generic build subcategories without CS codes
            if (ContainsAny(text, "inaccessible", "protection level"))
                return ("build", "inaccessible_member", string.Empty);

            if (ContainsAny(text, "ambiguous", "Ambiguous"))
                return ("build", "ambiguous_call", string.Empty);

            if (ContainsAny(text, "file-scoped namespace", "file scoped namespace"))
                return ("build", "file_scoped_namespace_conflict", string.Empty);

            if (ContainsAny(text, "missing reference", "Could not load", "not referenced"))
                return ("build", "missing_reference", string.Empty);

            return ("build", "compile_error_unknown", string.Empty);
        }

        private (string, string, string) LabelTestError(RunRecord record)
        {
            var text = CombineTexts(
                record.Error.RawMessage,
                record.Test.Stdout,
                record.Test.Stderr);

            // Filter mismatch / no tests found (check first — most important)
            if (FilterMismatchRegex.IsMatch(text))
                return ("test", "filter_mismatch", string.Empty);

            if (NoTestsRegex.IsMatch(text))
                return ("test", "no_tests_found", string.Empty);

            // Framework assertions
            if (XunitAssertionRegex.IsMatch(text))
                return ("test", "assertion_failed_xunit", "Xunit.Sdk");

            if (NunitAssertionRegex.IsMatch(text))
                return ("test", "assertion_failed_nunit", "NUnit.Framework");

            if (MsTestAssertionRegex.IsMatch(text))
                return ("test", "assertion_failed_mstest", "MSTest");

            // Runtime exceptions
            if (NullRefRegex.IsMatch(text))
                return ("test", "threw_exception_nullreference", "NullReferenceException");

            if (ArgNullRegex.IsMatch(text))
                return ("test", "threw_exception_argument", "ArgumentNullException");

            if (ArgRegex.IsMatch(text))
                return ("test", "threw_exception_argument", "ArgumentException");

            if (InvalidOpRegex.IsMatch(text))
                return ("test", "threw_exception_invalidoperation", "InvalidOperationException");

            // Timeout
            if (ContainsAny(text, "timeout", "Timeout", "timed out", "hung"))
                return ("test", "timeout_or_hang", string.Empty);

            return ("test", "test_failed_unknown", string.Empty);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string CombineTexts(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    sb.AppendLine(p);
                }
            }
            return sb.ToString();
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            foreach (var kw in keywords)
            {
                if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ── Load & Map ──────────────────────────────────────────────────────────

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

        private static RunFlatRow MapToFlatRow(RunRecord r,
            string labeledCategory, string labeledSubcategory, string primaryCode)
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

                // Error (original)
                ErrorCategory = r.Error.Category,
                ErrorSubcategory = r.Error.Subcategory,

                // Error labels (assigned by this command)
                ErrorCategoryLabeled = labeledCategory,
                ErrorSubcategoryLabeled = labeledSubcategory,
                LabelVersion = string.IsNullOrEmpty(labeledCategory) ? string.Empty : LabelVersionId,
                PrimaryErrorCode = primaryCode,

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

        // ── Summaries ───────────────────────────────────────────────────────────

        private void WriteSummaryByVariant(string labelsDir, List<RunFlatRow> rows)
        {
            var summary = rows
                .Where(r => !string.IsNullOrEmpty(r.ErrorCategoryLabeled))
                .GroupBy(r => new { r.Variant, Label = $"{r.ErrorCategoryLabeled}/{r.ErrorSubcategoryLabeled}" })
                .OrderBy(g => g.Key.Variant)
                .ThenBy(g => g.Key.Label)
                .Select(g => new
                {
                    g.Key.Variant,
                    g.Key.Label,
                    Count = g.Count()
                })
                .ToList();

            var path = Path.Combine(labelsDir, "label_summary_by_variant.csv");
            WriteCsv(path, summary);
            Console.WriteLine($"  Written: {path}");
        }

        private void WriteSummaryByStatus(string labelsDir, List<RunFlatRow> rows)
        {
            var summary = rows
                .Where(r => !string.IsNullOrEmpty(r.ErrorCategoryLabeled))
                .GroupBy(r => new
                {
                    r.Variant,
                    r.FinalStatus,
                    Label = $"{r.ErrorCategoryLabeled}/{r.ErrorSubcategoryLabeled}"
                })
                .OrderBy(g => g.Key.Variant)
                .ThenBy(g => g.Key.FinalStatus)
                .ThenBy(g => g.Key.Label)
                .Select(g => new
                {
                    g.Key.Variant,
                    g.Key.FinalStatus,
                    g.Key.Label,
                    Count = g.Count()
                })
                .ToList();

            var path = Path.Combine(labelsDir, "label_summary_by_status.csv");
            WriteCsv(path, summary);
            Console.WriteLine($"  Written: {path}");
        }

        // ── Markdown Report ─────────────────────────────────────────────────────

        private void WriteMarkdownReport(string labelsDir, List<RunFlatRow> rows,
            int parseOk, int parseFailed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Error Label Report");
            sb.AppendLine();
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Rules version: {LabelVersionId}");
            sb.AppendLine();

            // Overview
            sb.AppendLine("## Overview");
            sb.AppendLine();
            sb.AppendLine($"- JSON files parsed: **{parseOk}** (failed: {parseFailed})");
            sb.AppendLine($"- Total runs: **{rows.Count}**");
            var completedCount = rows.Count(r => r.FinalStatus == "completed");
            var labeledCount = rows.Count(r => !string.IsNullOrEmpty(r.ErrorCategoryLabeled));
            sb.AppendLine($"- Completed (no label): **{completedCount}**");
            sb.AppendLine($"- Labeled with errors: **{labeledCount}**");
            sb.AppendLine();

            // Top-level category breakdown
            sb.AppendLine("## Error Categories");
            sb.AppendLine();
            sb.AppendLine("| Category | Count | % of Labeled |");
            sb.AppendLine("|----------|-------|-------------|");
            var byCat = rows
                .Where(r => !string.IsNullOrEmpty(r.ErrorCategoryLabeled))
                .GroupBy(r => r.ErrorCategoryLabeled)
                .OrderByDescending(g => g.Count());
            foreach (var g in byCat)
            {
                var pct = labeledCount > 0
                    ? Math.Round(100.0 * g.Count() / labeledCount, 1) : 0;
                sb.AppendLine($"| {g.Key} | {g.Count()} | {pct}% |");
            }
            sb.AppendLine();

            // Detailed subcategory breakdown per variant
            sb.AppendLine("## Error Labels by Variant");
            sb.AppendLine();
            var variants = rows.Select(r => r.Variant).Distinct().OrderBy(v => v).ToList();
            foreach (var variant in variants)
            {
                var variantRows = rows
                    .Where(r => r.Variant == variant && !string.IsNullOrEmpty(r.ErrorCategoryLabeled))
                    .ToList();

                if (variantRows.Count == 0)
                    continue;

                sb.AppendLine($"### Variant {variant}");
                sb.AppendLine();
                sb.AppendLine("| Category | Subcategory | Code | Count |");
                sb.AppendLine("|----------|-------------|------|-------|");

                var groups = variantRows
                    .GroupBy(r => new { r.ErrorCategoryLabeled, r.ErrorSubcategoryLabeled, r.PrimaryErrorCode })
                    .OrderBy(g => g.Key.ErrorCategoryLabeled)
                    .ThenByDescending(g => g.Count());

                foreach (var g in groups)
                {
                    sb.AppendLine($"| {g.Key.ErrorCategoryLabeled} | {g.Key.ErrorSubcategoryLabeled} | {g.Key.PrimaryErrorCode} | {g.Count()} |");
                }
                sb.AppendLine();
            }

            // FinalStatus × Label cross table
            sb.AppendLine("## FinalStatus × Label Cross Table");
            sb.AppendLine();
            sb.AppendLine("| Variant | FinalStatus | Label | Count |");
            sb.AppendLine("|---------|-------------|-------|-------|");
            var crossRows = rows
                .Where(r => !string.IsNullOrEmpty(r.ErrorCategoryLabeled))
                .GroupBy(r => new { r.Variant, r.FinalStatus, Label = $"{r.ErrorCategoryLabeled}/{r.ErrorSubcategoryLabeled}" })
                .OrderBy(g => g.Key.Variant)
                .ThenBy(g => g.Key.FinalStatus)
                .ThenByDescending(g => g.Count());
            foreach (var g in crossRows)
            {
                sb.AppendLine($"| {g.Key.Variant} | {g.Key.FinalStatus} | {g.Key.Label} | {g.Count()} |");
            }
            sb.AppendLine();

            // Notes
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine($"- Labels are assigned by deterministic rules (version `{LabelVersionId}`).");
            sb.AppendLine("- `completed` runs receive no error label.");
            sb.AppendLine("- Build errors include the first CS error code found (e.g. CS0122).");
            sb.AppendLine("- Test errors distinguish assertion failures, runtime exceptions, and filter mismatches.");
            sb.AppendLine("- `filter_mismatch` indicates the generated test class name did not match the test filter.");
            sb.AppendLine("- Variant A = existing tests (baseline), B = single-shot LLM, C = repair-loop LLM.");

            var path = Path.Combine(labelsDir, "label_report.md");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"  Written: {path}");
        }
    }
}
