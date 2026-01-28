using System.CommandLine;
using ThesisExperiment.Commands;

// --- Load .env file if present (keys are NOT overwritten if already set) ---
var envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (!File.Exists(envPath))
    envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");

if (File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            continue;

        var separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex <= 0)
            continue;

        var key = trimmed[..separatorIndex].Trim();
        var value = trimmed[(separatorIndex + 1)..].Trim();

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            Environment.SetEnvironmentVariable(key, value);
    }
}

// --- select-projects ---
var selectOutputOption = new Option<string>("--output")
{
    Description = "Output directory for CSV files",
    DefaultValueFactory = _ => "appendix"
};

var selectProjectsCommand = new Command("select-projects", "Search GitHub and select candidate projects")
{
    selectOutputOption
};

selectProjectsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(selectOutputOption)!;
    var command = new SelectProjectsCommand();
    await command.ExecuteAsync(output);
});

// --- sample-methods ---
var sampleOutputOption = new Option<string>("--output")
{
    Description = "Output directory for CSV files (reads project_list_filtered.csv from here)",
    DefaultValueFactory = _ => "appendix"
};

var sampleMethodsCommand = new Command("sample-methods", "Select 5 projects and sample 10 methods each")
{
    sampleOutputOption
};

sampleMethodsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(sampleOutputOption)!;
    var command = new SampleMethodsCommand();
    await command.ExecuteAsync(output);
});

// --- collect-baseline ---
var baselineDataDirOption = new Option<string>("--data-dir")
{
    Description = "Directory containing method_list_all.csv and project_list_selected.csv",
    DefaultValueFactory = _ => "appendix"
};

var baselineLimitOption = new Option<int?>("--limit")
{
    Description = "Process only the first N methods (for dry-run testing)"
};

var collectBaselineCommand = new Command("collect-baseline",
    "Measure existing tests for all 50 focal methods (Variant A baseline)")
{
    baselineDataDirOption,
    baselineLimitOption
};

collectBaselineCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(baselineDataDirOption)!;
    var limit = parseResult.GetValue(baselineLimitOption);
    var command = new CollectBaselineCommand();
    await command.ExecuteAsync(output, limit);
});

// --- run-single-shot ---
var singleShotOutputOption = new Option<string>("--output")
{
    Description = "Directory containing method_list_all.csv and project_list_selected.csv",
    DefaultValueFactory = _ => "appendix"
};

var singleShotLimitOption = new Option<int?>("--limit")
{
    Description = "Process only the first N methods (for dry-run testing)"
};

var runSingleShotCommand = new Command("run-single-shot",
    "Generate tests with single-shot LLM prompting (Variant B)")
{
    singleShotOutputOption,
    singleShotLimitOption
};

runSingleShotCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(singleShotOutputOption)!;
    var limit = parseResult.GetValue(singleShotLimitOption);
    var command = new RunSingleShotCommand();
    await command.ExecuteAsync(output, limit);
});

// --- run-repair-loop ---
var repairOutputOption = new Option<string>("--output")
{
    Description = "Directory containing method_list_all.csv and project_list_selected.csv",
    DefaultValueFactory = _ => "appendix"
};

var repairMaxAttemptsOption = new Option<int>("--max-attempts")
{
    Description = "Maximum number of attempts per method (1 initial + N-1 repairs)",
    DefaultValueFactory = _ => 3
};

var repairLimitOption = new Option<int?>("--limit")
{
    Description = "Process only the first N methods (for dry-run testing)"
};

var runRepairLoopCommand = new Command("run-repair-loop",
    "Generate tests with repair-loop LLM prompting (Variant C)")
{
    repairOutputOption,
    repairMaxAttemptsOption,
    repairLimitOption
};

runRepairLoopCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(repairOutputOption)!;
    var maxAttempts = parseResult.GetValue(repairMaxAttemptsOption);
    var limit = parseResult.GetValue(repairLimitOption);
    var command = new RunRepairLoopCommand();
    await command.ExecuteAsync(output, maxAttempts, limit);
});

// --- aggregate-results ---
var aggregateRunsOption = new Option<string>("--runs")
{
    Description = "Directory containing RunRecord JSON files",
    DefaultValueFactory = _ => "runs"
};

var aggregateOutputOption = new Option<string>("--output")
{
    Description = "Output directory for aggregated CSV and report files",
    DefaultValueFactory = _ => "appendix"
};

var aggregateResultsCommand = new Command("aggregate-results",
    "Aggregate RunRecord JSONs into flat CSV and summary files")
{
    aggregateRunsOption,
    aggregateOutputOption
};

aggregateResultsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var runsDir = parseResult.GetValue(aggregateRunsOption)!;
    var output = parseResult.GetValue(aggregateOutputOption)!;
    var command = new AggregateResultsCommand();
    await command.ExecuteAsync(runsDir, output);
});

// --- label-errors ---
var labelRunsOption = new Option<string>("--runs")
{
    Description = "Directory containing RunRecord JSON files",
    DefaultValueFactory = _ => "runs"
};

var labelOutputOption = new Option<string>("--output")
{
    Description = "Output directory for labeled CSV and report files",
    DefaultValueFactory = _ => "appendix"
};

var labelErrorsCommand = new Command("label-errors",
    "Label RunRecord errors with standardised categories and subcategories")
{
    labelRunsOption,
    labelOutputOption
};

labelErrorsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var runsDir = parseResult.GetValue(labelRunsOption)!;
    var output = parseResult.GetValue(labelOutputOption)!;
    var command = new LabelErrorsCommand();
    await command.ExecuteAsync(runsDir, output);
});

// --- root ---
var rootCommand = new RootCommand("Thesis Experiment Tool")
{
    selectProjectsCommand,
    sampleMethodsCommand,
    collectBaselineCommand,
    runSingleShotCommand,
    runRepairLoopCommand,
    aggregateResultsCommand,
    labelErrorsCommand
};

return await rootCommand.Parse(args).InvokeAsync();
