using System.CommandLine;
using ThesisExperiment.Commands;

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

var collectBaselineCommand = new Command("collect-baseline",
    "Measure existing tests for all 50 focal methods (Variant A baseline)")
{
    baselineDataDirOption
};

collectBaselineCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(baselineDataDirOption)!;
    var command = new CollectBaselineCommand();
    await command.ExecuteAsync(output);
});

// --- root ---
var rootCommand = new RootCommand("Thesis Experiment Tool")
{
    selectProjectsCommand,
    sampleMethodsCommand,
    collectBaselineCommand
};

return await rootCommand.Parse(args).InvokeAsync();
