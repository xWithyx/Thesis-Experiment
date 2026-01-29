using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ThesisExperiment.Models;
using ThesisExperiment.Services;

namespace ThesisExperiment.Commands
{
    /// <summary>Generates tests via repair-loop LLM prompting (Variant C).</summary>
    public class RunRepairLoopCommand
    {
        private readonly GitCleanupService _git = new();
        private readonly DotnetBuildService _buildService = new();
        private readonly DotnetTestService _testService = new();
        private readonly CoverletService _coverletService = new();
        private readonly StrykerService _strykerService = new();
        private readonly JsonLogger _jsonLogger = new();
        private readonly OpenAiService _openAiService = new();
        private readonly RoslynMethodExtractor _roslynExtractor = new();
        private readonly PromptTemplateService _promptService = new();
        private readonly TestProjectDiscoveryService _discoveryService = new();

        private const string Variant = "C";
        private const string RunsDir = "runs";
        private const string RepairPolicyVersion = "RP-1.0";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Orchestrates repair-loop generation from Variant B records (RP-1.0).</summary>
        public async Task ExecuteAsync(string dataDir, int maxAttempts = 3, int? limit = null, int? skip = null)
        {
            Console.WriteLine($"=== Step 8: Run Repair Loop (Variant C, {RepairPolicyVersion}, max {maxAttempts} attempts) ===\n");

            // Load Variant B records
            Console.WriteLine($"Loading Variant B records from {RunsDir}...");
            var allBRecords = LoadVariantBRecords(RunsDir);
            Console.WriteLine($"Loaded {allBRecords.Count} Variant B records.");

            // Filter to repair targets
            var repairTargets = allBRecords.Where(IsRepairTarget).ToList();
            Console.WriteLine($"Repair targets: {repairTargets.Count} (build_failed, test_failed, or completed with 0% coverage)");

            // Log breakdown
            var buildFailed = repairTargets.Count(r => r.Outcome.FinalStatus == "build_failed");
            var testFailed = repairTargets.Count(r => r.Outcome.FinalStatus == "test_failed");
            var notFocal = repairTargets.Count(r => r.Outcome.FinalStatus == "completed");
            Console.WriteLine($"  - build_failed: {buildFailed}");
            Console.WriteLine($"  - test_failed: {testFailed}");
            Console.WriteLine($"  - completed (0% coverage): {notFocal}");

            if (skip.HasValue && skip.Value > 0)
            {
                repairTargets = repairTargets.Skip(skip.Value).ToList();
                Console.WriteLine($"  (--skip {skip.Value}: Skipped first {skip.Value} target(s), {repairTargets.Count} remaining)");
            }

            if (limit.HasValue && limit.Value > 0)
            {
                repairTargets = repairTargets.Take(limit.Value).ToList();
                Console.WriteLine($"  (--limit {limit.Value}: Processing only first {repairTargets.Count} target(s))");
            }

            // Load project stars
            var selectedPath = Path.Combine(dataDir, "project_list_selected.csv");
            var projectStarsMap = new Dictionary<string, int>();
            if (File.Exists(selectedPath))
            {
                var projects = ReadCsv<ProjectCandidate>(selectedPath);
                foreach (var p in projects)
                    projectStarsMap[p.RepoUrl] = p.Stars;
            }

            // Group by project
            var grouped = repairTargets.GroupBy(r => r.Project.RepoUrl).ToList();
            Console.WriteLine($"\nFound {grouped.Count} project(s) with repair targets.\n");

            int totalRecords = 0;
            int skippedExisting = 0;
            var statusCounts = new Dictionary<string, int>();
            var failureClassCounts = new Dictionary<string, int>();

            foreach (var projectGroup in grouped)
            {
                var repoUrl = projectGroup.Key;
                var projectTargets = projectGroup.ToList();
                var projectName = projectTargets.First().Project.Name;
                var commitHash = projectTargets.First().Project.CommitHash;

                Console.WriteLine($"=== [{projectName}] {projectTargets.Count} targets, commit {commitHash[..Math.Min(12, commitHash.Length)]} ===");

                string repoPath;
                try
                {
                    var clonePath = Path.Combine("repos", projectName);
                    repoPath = await _git.EnsureRepoAtCommitAsync(repoUrl, clonePath, commitHash);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: Failed to prepare repo: {ex.Message}");
                    continue;
                }

                foreach (var variantB in projectTargets)
                {
                    var methodId = variantB.Method.Identifier;
                    var containingClass = variantB.Method.ContainingClass;
                    var typeName = containingClass.Contains('.')
                        ? containingClass.Split('.').Last()
                        : containingClass;

                    Console.WriteLine($"\n  --- {typeName}.{methodId.Split('.').LastOrDefault() ?? methodId} ---");

                    // Idempotency: skip if C-record already exists
                    var cFilePath = JsonLogger.GetOutputPath(RunsDir, projectName, methodId, Variant);
                    if (File.Exists(cFilePath))
                    {
                        Console.WriteLine($"    Skipping (C-Record exists): {Path.GetFileName(cFilePath)}");
                        skippedExisting++;
                        continue;
                    }

                    // Determine failure class
                    var failureClass = DetermineFailureClass(variantB, typeName);
                    Console.WriteLine($"    FailureClass: {failureClass}");
                    IncrementStatus(failureClassCounts, failureClass);

                    try
                    {
                        var record = await ProcessMethodWithRepairFromBRecordAsync(
                            variantB, repoPath, projectStarsMap, maxAttempts, failureClass);

                        _jsonLogger.WriteRunRecord(record, cFilePath);
                        Console.WriteLine($"    Wrote: {Path.GetFileName(cFilePath)} [{record.Outcome.FinalStatus}] (attempt {record.Experiment.AttemptNumber}/{maxAttempts})");
                        Console.WriteLine($"    SuccessLevel: {record.Outcome.SuccessLevel}");

                        totalRecords++;
                        IncrementStatus(statusCounts, record.Outcome.FinalStatus);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    UNEXPECTED ERROR: {ex.Message}");
                        totalRecords++;
                        IncrementStatus(statusCounts, "internal_error");
                    }
                }
            }

            Console.WriteLine($"\n=== Repair Loop Run Complete ({RepairPolicyVersion}) ===");
            Console.WriteLine($"Total RunRecords written: {totalRecords}");
            Console.WriteLine($"Skipped (C-Record exists): {skippedExisting}");
            Console.WriteLine($"Projects processed:       {grouped.Count}");
            Console.WriteLine($"\nFailure class breakdown:");
            foreach (var kv in failureClassCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {kv.Key}: {kv.Value}");
            Console.WriteLine($"\nFinal status breakdown:");
            foreach (var kv in statusCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {kv.Key}: {kv.Value}");

            var expected = repairTargets.Count - skippedExisting;
            if (totalRecords == expected)
                Console.WriteLine($"\nAll {expected} targets processed. OK");
            else
                Console.WriteLine($"\nWARNING: Expected {expected} records, got {totalRecords}.");
        }

        /// <summary>Processes one focal method with repair loop: up to maxAttempts of prompt-generate-gate1-gate2.</summary>
        private async Task<RunRecord> ProcessMethodWithRepairAsync(
            SampledMethod method,
            string repoPath,
            string projectName,
            string repoUrl,
            string commitHash,
            Dictionary<string, int> starsMap,
            int maxAttempts)
        {
            var timestampStart = DateTime.UtcNow;

            Console.WriteLine("    Resetting repo to clean state...");
            await _git.ResetToCleanStateAsync(repoPath, commitHash);

            Console.WriteLine("    Discovering test project...");
            var testInfo = _discoveryService.DiscoverTestProject(repoPath, method.FilePath);
            if (testInfo == null)
            {
                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    maxAttempts: maxAttempts, attemptNumber: 0,
                    finalStatus: "no_test_project", stopReason: "no_test_project",
                    gate1Passed: false, gate2Passed: false,
                    errorCategory: "discovery", errorMessage: $"No test project found for {method.FilePath}");
            }

            Console.WriteLine($"    Test project: {Path.GetFileName(testInfo.TestProjectPath)} ({testInfo.Framework})");

            Console.WriteLine("    Extracting method source...");
            var absoluteFilePath = Path.Combine(repoPath, method.FilePath);
            var extraction = _roslynExtractor.ExtractMethod(
                absoluteFilePath, method.MethodName, method.LineStart, method.LineEnd, method.TypeName);

            if (extraction.UsedFallback)
                Console.WriteLine("    (used fallback line-range extraction)");

            var signature = !string.IsNullOrEmpty(extraction.MethodSignature)
                ? extraction.MethodSignature
                : $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})";

            string previousTest = "";
            string lastError = "";

            int cumulativePromptTokens = 0;
            int cumulativeCompletionTokens = 0;
            int cumulativeTotalTokens = 0;
            OpenAiResult? lastAiResult = null;
            PromptPair? lastPrompt = null;
            string lastPromptFile = "";
            BuildResult? lastBuildResult = null;
            TestResult? lastTestResult = null;

            var sanitizedId = SanitizeForFilename(method.Identifier);
            var generatedDir = Path.Combine(testInfo.TestProjectDir, "GeneratedTests");
            var generatedTestPath = Path.Combine(generatedDir, $"Generated__{sanitizedId}__C.cs");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Console.WriteLine($"\n    === Attempt {attempt}/{maxAttempts} ===");

                Console.WriteLine("    Resetting repo...");
                await _git.ResetToCleanStateAsync(repoPath, commitHash);
                Directory.CreateDirectory(generatedDir);

                PromptPair prompt;
                string promptFile;

                if (attempt == 1)
                {
                    Console.WriteLine("    Building single-shot prompt...");
                    prompt = _promptService.BuildSingleShotPrompt(
                        testInfo.Framework, testInfo.TestAttribute,
                        method.Namespace, method.TypeName, signature,
                        extraction.MethodBody, extraction.ClassContext);
                    promptFile = _promptService.SingleShotTemplateFile;
                }
                else
                {
                    Console.WriteLine($"    Building repair prompt (error from attempt {attempt - 1})...");
                    prompt = _promptService.BuildRepairPrompt(
                        testInfo.Framework, testInfo.TestAttribute,
                        method.Namespace, method.TypeName, signature,
                        extraction.MethodBody, previousTest, lastError);
                    promptFile = _promptService.RepairTemplateFile;
                }

                lastPrompt = prompt;
                lastPromptFile = promptFile;

                Console.WriteLine("    Calling OpenAI API...");
                OpenAiResult aiResult;
                try
                {
                    aiResult = await _openAiService.GenerateTestAsync(
                        prompt.SystemMessage, prompt.UserMessage);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    API error: {ex.Message}");
                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "api_error", stopReason: "api_error",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "openai", errorMessage: ex.Message,
                        prompt: lastPrompt, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                lastAiResult = aiResult;
                cumulativePromptTokens += aiResult.PromptTokens;
                cumulativeCompletionTokens += aiResult.CompletionTokens;
                cumulativeTotalTokens += aiResult.TotalTokens;

                Console.WriteLine($"    Model: {aiResult.ModelId}, Tokens: {aiResult.TotalTokens} (cumulative: {cumulativeTotalTokens})");

                var code = aiResult.ExtractedCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.WriteLine("    No code block found in response.");
                    lastError = "No code block extracted from LLM response";
                    previousTest = aiResult.RawText;

                    if (attempt < maxAttempts)
                        continue;

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "llm_invalid_output", stopReason: "llm_invalid_output",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "openai", errorSubcategory: "no_code_extracted",
                        errorMessage: "No code block found in LLM response",
                        prompt: lastPrompt, aiResult: lastAiResult, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                File.WriteAllText(generatedTestPath, code, Encoding.UTF8);
                Console.WriteLine($"    Wrote test file: {Path.GetFileName(generatedTestPath)}");

                var testQualifiedName = ExtractTestQualifiedName(code);
                var testFilter = testQualifiedName != null
                    ? $"FullyQualifiedName~{testQualifiedName}"
                    : null;

                if (testFilter != null)
                    Console.WriteLine($"    Test filter: {testFilter}");

                Console.WriteLine("    Gate 1: Building...");
                var buildResult = await _buildService.BuildAsync(repoPath);
                lastBuildResult = buildResult;
                bool buildPassed = buildResult.ExitCode == 0;
                Console.WriteLine($"    Gate 1: {(buildPassed ? "PASSED" : "FAILED")} (exit {buildResult.ExitCode})");

                if (!buildPassed)
                {
                    lastError = buildResult.Stderr;
                    previousTest = code;

                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine("    Will retry with repair prompt...");
                        continue;
                    }

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "build_failed", stopReason: "max_attempts_reached_gate1",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "build", errorMessage: buildResult.Stderr,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, promptFile: promptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                Console.WriteLine("    Gate 2: Running tests...");
                var testResult = await _testService.RunTestsOnlyAsync(repoPath, testFilter);
                lastTestResult = testResult;
                bool testPassed = testResult.ExitCode == 0;
                Console.WriteLine($"    Gate 2: {(testPassed ? "PASSED" : "FAILED")} (exit {testResult.ExitCode})");

                if (!testPassed)
                {
                    lastError = !string.IsNullOrWhiteSpace(testResult.Stderr)
                        ? testResult.Stderr
                        : testResult.Stdout;
                    previousTest = code;

                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine("    Will retry with repair prompt...");
                        continue;
                    }

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "test_failed", stopReason: "max_attempts_reached_gate2",
                        gate1Passed: true, gate2Passed: false,
                        errorCategory: "test", errorMessage: lastError,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: testResult, promptFile: promptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens);
                }

                Console.WriteLine($"    Attempt {attempt} PASSED both gates. Evaluating stability...");

                // Part 2: Flakiness check (no repo reset — generated test file must stay)
                var (isStablePass, flakiness, coverageDir, gate2Result) =
                    await EvaluateFlakinessAsync(repoPath, testFilter);

                if (gate2Result.ExitCode != 0)
                {
                    // Run 1 (with coverage) failed — treat as test_failed
                    var run1Error = !string.IsNullOrWhiteSpace(gate2Result.Stderr)
                        ? gate2Result.Stderr
                        : gate2Result.Stdout;

                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "test_failed", stopReason: "gate2_failed",
                        gate1Passed: true, gate2Passed: false,
                        errorCategory: "test", errorMessage: run1Error,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: gate2Result, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        flakiness: flakiness);
                }

                if (!isStablePass)
                {
                    // Flaky: Run 1 passed but Run 2 or 3 failed
                    return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "flaky_failure", stopReason: "flaky_test",
                        gate1Passed: true, gate2Passed: true,
                        errorCategory: "flakiness",
                        errorMessage: $"Inconsistent results: {string.Join(", ", flakiness.RunResults)}",
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: gate2Result, promptFile: promptFile,
                        previousTest: attempt > 1 ? previousTest : "",
                        errorFeedback: attempt > 1 ? lastError : "",
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        flakiness: flakiness);
                }

                // Stable pass: collect coverage + mutation
                Console.WriteLine("    Parsing coverage...");
                var coverage = CollectCoverage(repoPath, method, coverageDir, gate2Result.Stdout);
                Console.WriteLine($"    Coverage: {coverage.LinePercent}% line, {coverage.BranchPercent}% branch (status: {coverage.Status})");

                Console.WriteLine("    Running Stryker mutation testing...");
                var cacheKey = $"{projectName}::{commitHash}::{method.FilePath}";
                var mutation = await CollectMutationAsync(repoPath, method, cacheKey);
                Console.WriteLine($"    Mutation: {mutation.MutationScore}% ({mutation.MutantsKilled}/{mutation.MutantsTotal}) [status: {mutation.Status}]");

                return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                    maxAttempts: maxAttempts, attemptNumber: attempt,
                    finalStatus: "completed", stopReason: "completed",
                    gate1Passed: true, gate2Passed: true,
                    prompt: lastPrompt, aiResult: lastAiResult,
                    buildResult: buildResult, testResult: gate2Result, promptFile: promptFile,
                    previousTest: attempt > 1 ? previousTest : "",
                    errorFeedback: attempt > 1 ? lastError : "",
                    cumulativePromptTokens: cumulativePromptTokens,
                    cumulativeCompletionTokens: cumulativeCompletionTokens,
                    cumulativeTotalTokens: cumulativeTotalTokens,
                    coverage: coverage, mutation: mutation, flakiness: flakiness);
            }

            return BuildRecord(timestampStart, projectName, method, repoUrl, commitHash, starsMap,
                maxAttempts: maxAttempts, attemptNumber: maxAttempts,
                finalStatus: "internal_error", stopReason: "unexpected_loop_exit",
                gate1Passed: false, gate2Passed: false,
                errorCategory: "internal", errorMessage: "Attempt loop exited unexpectedly");
        }

        /// <summary>Processes a repair target from a Variant B record (RP-1.0).</summary>
        private async Task<RunRecord> ProcessMethodWithRepairFromBRecordAsync(
            RunRecord variantB,
            string repoPath,
            Dictionary<string, int> starsMap,
            int maxAttempts,
            string failureClass)
        {
            var timestampStart = DateTime.UtcNow;
            var projectName = variantB.Project.Name;
            var repoUrl = variantB.Project.RepoUrl;
            var commitHash = variantB.Project.CommitHash;
            var sourceRunId = variantB.RunId;

            // Extract type and method names
            var containingClass = variantB.Method.ContainingClass;
            var typeName = containingClass.Contains('.')
                ? containingClass.Split('.').Last()
                : containingClass;
            var namespaceName = containingClass.Contains('.')
                ? string.Join('.', containingClass.Split('.').SkipLast(1))
                : "";
            var methodSignature = variantB.Method.Signature;
            var methodName = methodSignature.Contains('(')
                ? methodSignature.Split('(')[0].Split(' ').Last()
                : methodSignature;

            Console.WriteLine("    Resetting repo to clean state...");
            await _git.ResetToCleanStateAsync(repoPath, commitHash);

            Console.WriteLine("    Discovering test project...");
            var testInfo = _discoveryService.DiscoverTestProject(repoPath, variantB.Method.FilePath);
            if (testInfo == null)
            {
                return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                    maxAttempts: maxAttempts, attemptNumber: 0,
                    finalStatus: "no_test_project", stopReason: "no_test_project",
                    gate1Passed: false, gate2Passed: false,
                    errorCategory: "discovery", errorMessage: $"No test project found for {variantB.Method.FilePath}",
                    failureClass: failureClass, successLevel: "");
            }

            Console.WriteLine($"    Test project: {Path.GetFileName(testInfo.TestProjectPath)} ({testInfo.Framework})");

            Console.WriteLine("    Extracting method source...");
            var absoluteFilePath = Path.Combine(repoPath, variantB.Method.FilePath);
            var extraction = _roslynExtractor.ExtractMethod(
                absoluteFilePath, methodName, variantB.Method.LineStart, variantB.Method.LineEnd, typeName);

            if (extraction.UsedFallback)
                Console.WriteLine("    (used fallback line-range extraction)");

            var signature = !string.IsNullOrEmpty(extraction.MethodSignature)
                ? extraction.MethodSignature
                : methodSignature;

            // Initialize from B-record
            string previousTest = variantB.Response.ExtractedCode ?? "";
            string lastError = failureClass switch
            {
                "build_failed" => variantB.Build.Stderr ?? variantB.Build.Stdout ?? "",
                "test_failed" => variantB.Test.Stderr ?? variantB.Test.Stdout ?? "",
                "not_focal_copy" or "not_focal" => GetNotFocalErrorMessage(variantB, typeName, methodName),
                _ => ""
            };

            Console.WriteLine($"    Initial error type: {failureClass}");

            int cumulativePromptTokens = 0;
            int cumulativeCompletionTokens = 0;
            int cumulativeTotalTokens = 0;
            OpenAiResult? lastAiResult = null;
            PromptPair? lastPrompt = null;
            string lastPromptFile = "";
            BuildResult? lastBuildResult = null;
            TestResult? lastTestResult = null;

            var sanitizedId = SanitizeForFilename(variantB.Method.Identifier);
            var generatedDir = Path.Combine(testInfo.TestProjectDir, "GeneratedTests");
            var generatedTestPath = Path.Combine(generatedDir, $"Generated__{sanitizedId}__C.cs");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Console.WriteLine($"\n    === Attempt {attempt}/{maxAttempts} ===");

                Console.WriteLine("    Resetting repo...");
                await _git.ResetToCleanStateAsync(repoPath, commitHash);
                Directory.CreateDirectory(generatedDir);

                // Always use repair prompt (we're repairing from B-record)
                Console.WriteLine($"    Building repair prompt...");
                var prompt = _promptService.BuildRepairPrompt(
                    testInfo.Framework, testInfo.TestAttribute,
                    namespaceName, typeName, signature,
                    extraction.MethodBody, previousTest, lastError);
                lastPrompt = prompt;
                lastPromptFile = _promptService.RepairTemplateFile;

                Console.WriteLine("    Calling OpenAI API...");
                OpenAiResult aiResult;
                try
                {
                    aiResult = await _openAiService.GenerateTestAsync(
                        prompt.SystemMessage, prompt.UserMessage);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    API error: {ex.Message}");
                    return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "api_error", stopReason: "api_error",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "openai", errorMessage: ex.Message,
                        prompt: lastPrompt, promptFile: lastPromptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        failureClass: failureClass, successLevel: "");
                }

                lastAiResult = aiResult;
                cumulativePromptTokens += aiResult.PromptTokens;
                cumulativeCompletionTokens += aiResult.CompletionTokens;
                cumulativeTotalTokens += aiResult.TotalTokens;

                Console.WriteLine($"    Model: {aiResult.ModelId}, Tokens: {aiResult.TotalTokens} (cumulative: {cumulativeTotalTokens})");

                var code = aiResult.ExtractedCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    Console.WriteLine("    No code block found in response.");
                    lastError = "No code block extracted from LLM response";
                    previousTest = aiResult.RawText;

                    if (attempt < maxAttempts)
                        continue;

                    return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "llm_invalid_output", stopReason: "llm_invalid_output",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "openai", errorSubcategory: "no_code_extracted",
                        errorMessage: "No code block found in LLM response",
                        prompt: lastPrompt, aiResult: lastAiResult, promptFile: lastPromptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        failureClass: failureClass, successLevel: "");
                }

                File.WriteAllText(generatedTestPath, code, Encoding.UTF8);
                Console.WriteLine($"    Wrote test file: {Path.GetFileName(generatedTestPath)}");

                var testQualifiedName = ExtractTestQualifiedName(code);
                var testFilter = testQualifiedName != null
                    ? $"FullyQualifiedName~{testQualifiedName}"
                    : null;

                if (testFilter != null)
                    Console.WriteLine($"    Test filter: {testFilter}");

                Console.WriteLine("    Gate 1: Building...");
                var buildResult = await _buildService.BuildAsync(repoPath);
                lastBuildResult = buildResult;
                bool buildPassed = buildResult.ExitCode == 0;
                Console.WriteLine($"    Gate 1: {(buildPassed ? "PASSED" : "FAILED")} (exit {buildResult.ExitCode})");

                if (!buildPassed)
                {
                    lastError = buildResult.Stderr;
                    previousTest = code;

                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine("    Will retry with repair prompt...");
                        continue;
                    }

                    return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "build_failed", stopReason: "max_attempts_reached_gate1",
                        gate1Passed: false, gate2Passed: false,
                        errorCategory: "build", errorMessage: buildResult.Stderr,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, promptFile: lastPromptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        failureClass: failureClass, successLevel: "compiled");
                }

                Console.WriteLine("    Gate 2: Running tests...");
                var testResult = await _testService.RunTestsOnlyAsync(repoPath, testFilter);
                lastTestResult = testResult;
                bool testPassed = testResult.ExitCode == 0;
                Console.WriteLine($"    Gate 2: {(testPassed ? "PASSED" : "FAILED")} (exit {testResult.ExitCode})");

                if (!testPassed)
                {
                    lastError = !string.IsNullOrWhiteSpace(testResult.Stderr)
                        ? testResult.Stderr
                        : testResult.Stdout;
                    previousTest = code;

                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine("    Will retry with repair prompt...");
                        continue;
                    }

                    return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "test_failed", stopReason: "max_attempts_reached_gate2",
                        gate1Passed: true, gate2Passed: false,
                        errorCategory: "test", errorMessage: lastError,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: testResult, promptFile: lastPromptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        failureClass: failureClass, successLevel: "compiled");
                }

                Console.WriteLine($"    Attempt {attempt} PASSED both gates. Evaluating stability...");

                // Part 2: Flakiness check
                var (isStablePass, flakiness, coverageDir, gate2Result) =
                    await EvaluateFlakinessAsync(repoPath, testFilter);

                if (gate2Result.ExitCode != 0)
                {
                    var run1Error = !string.IsNullOrWhiteSpace(gate2Result.Stderr)
                        ? gate2Result.Stderr
                        : gate2Result.Stdout;

                    return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "test_failed", stopReason: "gate2_failed",
                        gate1Passed: true, gate2Passed: false,
                        errorCategory: "test", errorMessage: run1Error,
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: gate2Result, promptFile: lastPromptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        flakiness: flakiness,
                        failureClass: failureClass, successLevel: "compiled");
                }

                if (!isStablePass)
                {
                    return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                        maxAttempts: maxAttempts, attemptNumber: attempt,
                        finalStatus: "flaky_failure", stopReason: "flaky_test",
                        gate1Passed: true, gate2Passed: true,
                        errorCategory: "flakiness",
                        errorMessage: $"Inconsistent results: {string.Join(", ", flakiness.RunResults)}",
                        prompt: lastPrompt, aiResult: lastAiResult,
                        buildResult: buildResult, testResult: gate2Result, promptFile: lastPromptFile,
                        previousTest: previousTest, errorFeedback: lastError,
                        cumulativePromptTokens: cumulativePromptTokens,
                        cumulativeCompletionTokens: cumulativeCompletionTokens,
                        cumulativeTotalTokens: cumulativeTotalTokens,
                        flakiness: flakiness,
                        failureClass: failureClass, successLevel: "green_not_focal");
                }

                // Stable pass: collect coverage + mutation
                Console.WriteLine("    Parsing coverage...");
                var coverage = CollectCoverageFromBRecord(repoPath, variantB, coverageDir, gate2Result.Stdout);
                Console.WriteLine($"    Coverage: {coverage.LinePercent}% line, {coverage.BranchPercent}% branch (status: {coverage.Status})");

                var successLevel = DetermineSuccessLevel(true, true, coverage.LinePercent ?? 0);
                Console.WriteLine($"    SuccessLevel: {successLevel}");

                Console.WriteLine("    Running Stryker mutation testing...");
                var cacheKey = $"{projectName}::{commitHash}::{variantB.Method.FilePath}";
                var mutation = await CollectMutationFromBRecordAsync(repoPath, variantB, cacheKey);
                Console.WriteLine($"    Mutation: {mutation.MutationScore}% ({mutation.MutantsKilled}/{mutation.MutantsTotal}) [status: {mutation.Status}]");

                return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                    maxAttempts: maxAttempts, attemptNumber: attempt,
                    finalStatus: "completed", stopReason: "completed",
                    gate1Passed: true, gate2Passed: true,
                    prompt: lastPrompt, aiResult: lastAiResult,
                    buildResult: buildResult, testResult: gate2Result, promptFile: lastPromptFile,
                    previousTest: previousTest, errorFeedback: lastError,
                    cumulativePromptTokens: cumulativePromptTokens,
                    cumulativeCompletionTokens: cumulativeCompletionTokens,
                    cumulativeTotalTokens: cumulativeTotalTokens,
                    coverage: coverage, mutation: mutation, flakiness: flakiness,
                    failureClass: failureClass, successLevel: successLevel);
            }

            return BuildRecordFromBRecord(timestampStart, variantB, starsMap,
                maxAttempts: maxAttempts, attemptNumber: maxAttempts,
                finalStatus: "internal_error", stopReason: "unexpected_loop_exit",
                gate1Passed: false, gate2Passed: false,
                errorCategory: "internal", errorMessage: "Attempt loop exited unexpectedly",
                failureClass: failureClass, successLevel: "");
        }

        /// <summary>Builds a RunRecord from a B-record context (RP-1.0).</summary>
        private RunRecord BuildRecordFromBRecord(
            DateTime timestampStart,
            RunRecord variantB,
            Dictionary<string, int> starsMap,
            int maxAttempts,
            int attemptNumber,
            string finalStatus,
            string stopReason,
            bool gate1Passed,
            bool gate2Passed,
            string failureClass,
            string successLevel,
            string errorCategory = "",
            string errorSubcategory = "",
            string errorMessage = "",
            PromptPair? prompt = null,
            OpenAiResult? aiResult = null,
            BuildResult? buildResult = null,
            TestResult? testResult = null,
            string promptFile = "",
            string previousTest = "",
            string errorFeedback = "",
            int cumulativePromptTokens = 0,
            int cumulativeCompletionTokens = 0,
            int cumulativeTotalTokens = 0,
            CoverageResult? coverage = null,
            MutationResult? mutation = null,
            FlakinessResult? flakiness = null)
        {
            var effectivePromptFile = !string.IsNullOrEmpty(promptFile)
                ? promptFile
                : _promptService.RepairTemplateFile;

            return new RunRecord
            {
                RunId = $"C__{variantB.Project.Name}__{SanitizeForId(variantB.Method.Identifier)}",
                TimestampStart = timestampStart,
                TimestampEnd = DateTime.UtcNow,
                Project = new ProjectInfo
                {
                    Name = variantB.Project.Name,
                    RepoUrl = variantB.Project.RepoUrl,
                    CommitHash = variantB.Project.CommitHash,
                    Stars = starsMap.GetValueOrDefault(variantB.Project.RepoUrl, variantB.Project.Stars)
                },
                Method = new MethodInfo
                {
                    Identifier = variantB.Method.Identifier,
                    FilePath = variantB.Method.FilePath,
                    LineStart = variantB.Method.LineStart,
                    LineEnd = variantB.Method.LineEnd,
                    Signature = variantB.Method.Signature,
                    ContainingClass = variantB.Method.ContainingClass,
                    ContainingFile = variantB.Method.ContainingFile
                },
                Experiment = new ExperimentInfo
                {
                    Variant = Variant,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    PromptVersion = _promptService.TemplateVersion,
                    PromptFile = effectivePromptFile,
                    RepairPolicyVersion = RepairPolicyVersion,
                    SourceVariantBRunId = variantB.RunId
                },
                Model = aiResult != null
                    ? new ModelInfo
                    {
                        Provider = "openai",
                        ModelId = aiResult.ModelId,
                        Temperature = 0.2,
                        MaxTokens = 2048,
                        RequestId = aiResult.RequestId
                    }
                    : new ModelInfo(),
                Prompt = prompt != null
                    ? new PromptInfo
                    {
                        SystemMessage = prompt.SystemMessage,
                        UserMessage = prompt.UserMessage,
                        TemplateFile = effectivePromptFile,
                        TemplateVersion = _promptService.TemplateVersion,
                        PreviousTest = previousTest,
                        ErrorFeedback = errorFeedback
                    }
                    : new PromptInfo(),
                Response = aiResult != null
                    ? new ResponseInfo
                    {
                        RawText = aiResult.RawText,
                        ExtractedCode = aiResult.ExtractedCode,
                        TokensUsed = cumulativeTotalTokens,
                        PromptTokens = cumulativePromptTokens,
                        CompletionTokens = cumulativeCompletionTokens,
                        FinishReason = aiResult.FinishReason
                    }
                    : new ResponseInfo(),
                Build = buildResult ?? new BuildResult(),
                Test = testResult ?? new TestResult(),
                Coverage = coverage ?? new CoverageResult(),
                Mutation = mutation ?? new MutationResult(),
                Flakiness = flakiness ?? new FlakinessResult(),
                Outcome = new OutcomeInfo
                {
                    Gate1BuildPassed = gate1Passed,
                    Gate2TestPassed = gate2Passed,
                    FinalStatus = finalStatus,
                    StopReason = stopReason,
                    FailureClass = failureClass,
                    SuccessLevel = successLevel
                },
                Error = !string.IsNullOrEmpty(errorCategory)
                    ? new ErrorInfo
                    {
                        Category = errorCategory,
                        Subcategory = errorSubcategory,
                        RawMessage = Truncate(errorMessage, 10_000),
                        LabeledBy = "automation"
                    }
                    : new ErrorInfo()
            };
        }

        /// <summary>Collects coverage from B-record context.</summary>
        private CoverageResult CollectCoverageFromBRecord(string repoPath, RunRecord variantB, string coverageDir, string? testStdout)
        {
            var coberturaFiles = _coverletService.FindCoberturaFiles(coverageDir);
            return _coverletService.ParseMethodCoverage(
                coberturaFiles, variantB.Method.FilePath, variantB.Method.LineStart, variantB.Method.LineEnd, repoPath, testStdout);
        }

        /// <summary>Runs Stryker mutation testing from B-record context.</summary>
        private async Task<MutationResult> CollectMutationFromBRecordAsync(string repoPath, RunRecord variantB, string cacheKey)
        {
            try
            {
                var (fileResult, status) = await _strykerService.RunStrykerForFileAsync(
                    repoPath, variantB.Method.FilePath, cacheKey);
                return _strykerService.ExtractMethodMutation(
                    fileResult, status, variantB.Method.FilePath, variantB.Method.LineStart, variantB.Method.LineEnd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Stryker failed: {ex.Message}");
                return new MutationResult { Status = "error", Note = ex.Message };
            }
        }

        private RunRecord BuildRecord(
            DateTime timestampStart,
            string projectName,
            SampledMethod method,
            string repoUrl,
            string commitHash,
            Dictionary<string, int> starsMap,
            int maxAttempts,
            int attemptNumber,
            string finalStatus,
            string stopReason,
            bool gate1Passed,
            bool gate2Passed,
            string errorCategory = "",
            string errorSubcategory = "",
            string errorMessage = "",
            PromptPair? prompt = null,
            OpenAiResult? aiResult = null,
            BuildResult? buildResult = null,
            TestResult? testResult = null,
            string promptFile = "",
            string previousTest = "",
            string errorFeedback = "",
            int cumulativePromptTokens = 0,
            int cumulativeCompletionTokens = 0,
            int cumulativeTotalTokens = 0,
            CoverageResult? coverage = null,
            MutationResult? mutation = null,
            FlakinessResult? flakiness = null,
            string sourceVariantBRunId = "",
            string failureClass = "",
            string successLevel = "")
        {
            var csvSignature = $"{method.ReturnType} {method.MethodName}({method.ParameterTypes})";
            var effectivePromptFile = !string.IsNullOrEmpty(promptFile)
                ? promptFile
                : _promptService.SingleShotTemplateFile;

            return new RunRecord
            {
                RunId = $"C__{projectName}__{SanitizeForId(method.Identifier)}",
                TimestampStart = timestampStart,
                TimestampEnd = DateTime.UtcNow,
                Project = new ProjectInfo
                {
                    Name = projectName,
                    RepoUrl = repoUrl,
                    CommitHash = commitHash,
                    Stars = starsMap.GetValueOrDefault(repoUrl, 0)
                },
                Method = new MethodInfo
                {
                    Identifier = method.Identifier,
                    FilePath = method.FilePath,
                    LineStart = method.LineStart,
                    LineEnd = method.LineEnd,
                    Signature = csvSignature,
                    ContainingClass = $"{method.Namespace}.{method.TypeName}",
                    ContainingFile = method.FilePath
                },
                Experiment = new ExperimentInfo
                {
                    Variant = Variant,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    PromptVersion = _promptService.TemplateVersion,
                    PromptFile = effectivePromptFile,
                    RepairPolicyVersion = RepairPolicyVersion,
                    SourceVariantBRunId = sourceVariantBRunId
                },
                Model = aiResult != null
                    ? new ModelInfo
                    {
                        Provider = "openai",
                        ModelId = aiResult.ModelId,
                        Temperature = 0.2,
                        MaxTokens = 2048,
                        RequestId = aiResult.RequestId
                    }
                    : new ModelInfo(),
                Prompt = prompt != null
                    ? new PromptInfo
                    {
                        SystemMessage = prompt.SystemMessage,
                        UserMessage = prompt.UserMessage,
                        TemplateFile = effectivePromptFile,
                        TemplateVersion = _promptService.TemplateVersion,
                        PreviousTest = previousTest,
                        ErrorFeedback = errorFeedback
                    }
                    : new PromptInfo(),
                Response = aiResult != null
                    ? new ResponseInfo
                    {
                        RawText = aiResult.RawText,
                        ExtractedCode = aiResult.ExtractedCode,
                        TokensUsed = cumulativeTotalTokens,
                        PromptTokens = cumulativePromptTokens,
                        CompletionTokens = cumulativeCompletionTokens,
                        FinishReason = aiResult.FinishReason
                    }
                    : new ResponseInfo(),
                Build = buildResult ?? new BuildResult(),
                Test = testResult ?? new TestResult(),
                Coverage = coverage ?? new CoverageResult(),
                Mutation = mutation ?? new MutationResult(),
                Flakiness = flakiness ?? new FlakinessResult(),
                Outcome = new OutcomeInfo
                {
                    Gate1BuildPassed = gate1Passed,
                    Gate2TestPassed = gate2Passed,
                    FinalStatus = finalStatus,
                    StopReason = stopReason,
                    FailureClass = failureClass,
                    SuccessLevel = successLevel
                },
                Error = !string.IsNullOrEmpty(errorCategory)
                    ? new ErrorInfo
                    {
                        Category = errorCategory,
                        Subcategory = errorSubcategory,
                        RawMessage = Truncate(errorMessage, 10_000),
                        LabeledBy = "automation"
                    }
                    : new ErrorInfo()
            };
        }

        /// <summary>Runs 3 test passes (1 with coverage, 2 without) to check stability.</summary>
        private async Task<(bool IsStablePass, FlakinessResult Flakiness, string CoverageDir, TestResult Gate2Result)>
            EvaluateFlakinessAsync(string repoPath, string? testFilter)
        {
            var results = new List<string>();

            // Run 1: with coverage collection
            Console.WriteLine("    Flakiness run 1 (with coverage)...");
            var (gate2, coverageDir) = await _testService.RunTestsWithCoverageAsync(repoPath, testFilter);
            var run1Result = gate2.ExitCode == 0 ? "pass" : "fail";
            results.Add(run1Result);
            Console.WriteLine($"    Flakiness run 1: {run1Result}");

            // Run 2 and 3: without coverage
            for (int i = 2; i <= 3; i++)
            {
                var rerun = await _testService.RunTestsOnlyAsync(repoPath, testFilter);
                var runResult = rerun.ExitCode == 0 ? "pass" : "fail";
                results.Add(runResult);
                Console.WriteLine($"    Flakiness run {i}: {runResult}");
            }

            var stable = results.Distinct().Count() == 1 && results[0] == "pass";
            var classification = stable ? "stable_pass" : "flaky";
            Console.WriteLine($"    Flakiness: {classification}");

            var flakiness = new FlakinessResult
            {
                RunsExecuted = 3,
                RunResults = results,
                Classification = classification
            };

            return (stable, flakiness, coverageDir, gate2);
        }

        /// <summary>Parses Coverlet/Cobertura coverage for the focal method.</summary>
        private CoverageResult CollectCoverage(string repoPath, SampledMethod method, string coverageDir, string? testStdout)
        {
            var coberturaFiles = _coverletService.FindCoberturaFiles(coverageDir);
            return _coverletService.ParseMethodCoverage(
                coberturaFiles, method.FilePath, method.LineStart, method.LineEnd, repoPath, testStdout);
        }

        /// <summary>Runs Stryker mutation testing for the focal method's source file.</summary>
        private async Task<MutationResult> CollectMutationAsync(
            string repoPath, SampledMethod method, string cacheKey)
        {
            try
            {
                var (fileResult, status) = await _strykerService.RunStrykerForFileAsync(
                    repoPath, method.FilePath, cacheKey);
                return _strykerService.ExtractMethodMutation(
                    fileResult, status, method.FilePath, method.LineStart, method.LineEnd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Stryker failed: {ex.Message}");
                return new MutationResult { Status = "error", Note = ex.Message };
            }
        }

        private void WriteFailedRecord(
            string projectName, SampledMethod method, string repoUrl, string commitHash,
            Dictionary<string, int> starsMap, int maxAttempts, string finalStatus,
            string errorCategory, string errorMessage)
        {
            var record = BuildRecord(
                DateTime.UtcNow, projectName, method, repoUrl, commitHash, starsMap,
                maxAttempts: maxAttempts, attemptNumber: 0,
                finalStatus: finalStatus, stopReason: finalStatus,
                gate1Passed: false, gate2Passed: false,
                errorCategory: errorCategory, errorMessage: errorMessage);

            var filePath = JsonLogger.GetOutputPath(RunsDir, projectName, method.Identifier, Variant);
            _jsonLogger.WriteRunRecord(record, filePath);
        }

        private static string? ExtractTestQualifiedName(string code)
        {
            var classMatch = Regex.Match(code, @"class\s+(\w+)");
            if (!classMatch.Success)
                return null;

            var className = classMatch.Groups[1].Value;

            var nsMatch = Regex.Match(code, @"namespace\s+([\w.]+)");
            if (nsMatch.Success)
                return $"{nsMatch.Groups[1].Value}.{className}";

            return className;
        }

        private static string SanitizeForId(string input)
        {
            return input.Replace(' ', '_').Replace('.', '_');
        }

        private static string SanitizeForFilename(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
                sb.Append(invalid.Contains(c) ? '_' : c);

            return sb.ToString()
                .Replace('(', '_')
                .Replace(')', '_')
                .Replace(',', '_')
                .Replace(' ', '_');
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text[..maxLength] + $"\n... [truncated, {text.Length} total chars]";
        }

        private static void IncrementStatus(Dictionary<string, int> counts, string status)
        {
            counts.TryGetValue(status, out var current);
            counts[status] = current + 1;
        }

        private List<T> ReadCsv<T>(string filePath)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HeaderValidated = null,
                MissingFieldFound = null
            };

            using var reader = new StreamReader(filePath, Encoding.UTF8);
            using var csv = new CsvReader(reader, config);
            return csv.GetRecords<T>().ToList();
        }

        // ───────────────────────── RP-1.0 Helper Methods ─────────────────────────

        /// <summary>Loads all Variant B RunRecords from the runs directory.</summary>
        private List<RunRecord> LoadVariantBRecords(string runsDir)
        {
            var records = new List<RunRecord>();
            if (!Directory.Exists(runsDir))
                return records;

            var jsonFiles = Directory.GetFiles(runsDir, "*_B.json", SearchOption.TopDirectoryOnly);
            foreach (var file in jsonFiles)
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var record = JsonSerializer.Deserialize<RunRecord>(json, JsonOptions);
                    if (record != null)
                        records.Add(record);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: Failed to parse {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            return records;
        }

        /// <summary>Checks if the build output contains CS0436 (type conflict from copy).</summary>
        private static bool IsCopyConflict(RunRecord variantB)
        {
            var buildOutput = (variantB.Build.Stdout ?? "") + (variantB.Build.Stderr ?? "");
            return buildOutput.Contains("CS0436");
        }

        /// <summary>Checks if the generated test code contains a duplicate type definition.</summary>
        private static bool HasDuplicateTypeDefinition(RunRecord variantB, string typeName)
        {
            var code = variantB.Response.ExtractedCode ?? "";
            // Look for top-level class definition (not the test class)
            var pattern = $@"(?<!Test)\s*class\s+{Regex.Escape(typeName)}\b";
            return Regex.IsMatch(code, pattern);
        }

        /// <summary>Determines the failure class from a Variant B record.</summary>
        private static string DetermineFailureClass(RunRecord variantB, string typeName)
        {
            if (variantB.Outcome.FinalStatus == "build_failed")
                return "build_failed";
            if (variantB.Outcome.FinalStatus == "test_failed")
                return "test_failed";

            // completed with Coverage 0% + copy detection
            if (variantB.Outcome.FinalStatus == "completed" && variantB.Coverage.LinePercent == 0)
            {
                if (IsCopyConflict(variantB) || HasDuplicateTypeDefinition(variantB, typeName))
                    return "not_focal_copy";
                return "not_focal";
            }

            return "unknown";
        }

        /// <summary>Builds the error message for not_focal/not_focal_copy cases.</summary>
        private static string GetNotFocalErrorMessage(RunRecord variantB, string className, string methodName)
        {
            var sb = new StringBuilder();

            // CS0436 warning as first line if present
            var buildOutput = (variantB.Build.Stdout ?? "") + (variantB.Build.Stderr ?? "");
            var cs0436Match = Regex.Match(buildOutput, @"warning CS0436[^\r\n]*");
            if (cs0436Match.Success)
                sb.AppendLine($"Compiler warning detected: {cs0436Match.Value}");

            sb.AppendLine();
            sb.AppendLine("The test passes but does not execute the focal method. You MUST:");
            sb.AppendLine("1. REMOVE any class/interface definitions that duplicate types from the project");
            sb.AppendLine("2. ADD proper 'using' statements to import the REAL types from the project");
            sb.AppendLine($"3. Instantiate and call the REAL {className}.{methodName} from the production assembly");
            sb.AppendLine("4. DO NOT recreate the focal class or its dependencies in the test file");
            sb.AppendLine();
            sb.AppendLine("Additional constraints:");
            sb.AppendLine("- Do not define any namespace-level classes except the single test class");
            sb.AppendLine("- If you need stubs, use local functions inside the test method only");
            sb.AppendLine();
            sb.AppendLine("The test must call the real focal method at least once and assert a deterministic outcome.");

            return sb.ToString();
        }

        /// <summary>Determines the success level from gate results and coverage.</summary>
        private static string DetermineSuccessLevel(bool gate1, bool gate2, double lineCoverage)
        {
            if (!gate1) return "";                       // Build failed
            if (!gate2) return "compiled";               // Build OK, Tests failed
            if (lineCoverage > 0) return "focal_executed";  // Tests OK + Focal reached
            return "green_not_focal";                    // Tests OK but Focal not reached
        }

        /// <summary>Checks if a repair target should be processed (filter criteria for RP-1.0).</summary>
        private static bool IsRepairTarget(RunRecord variantB)
        {
            // Repair targets: build_failed, test_failed, or completed with 0% coverage
            if (variantB.Outcome.FinalStatus == "build_failed") return true;
            if (variantB.Outcome.FinalStatus == "test_failed") return true;
            if (variantB.Outcome.FinalStatus == "completed" && variantB.Coverage.LinePercent == 0) return true;
            return false;
        }
    }
}
