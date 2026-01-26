using System.CommandLine;
using ThesisExperiment.Commands;

var outputOption = new Option<string>("--output")
{
    Description = "Output directory for CSV files",
    DefaultValueFactory = _ => "appendix"
};

var selectProjectsCommand = new Command("select-projects", "Search GitHub and select candidate projects")
{
    outputOption
};

selectProjectsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(outputOption)!;
    var command = new SelectProjectsCommand();
    await command.ExecuteAsync(output);
});

var rootCommand = new RootCommand("Thesis Experiment Tool")
{
    selectProjectsCommand
};

return await rootCommand.Parse(args).InvokeAsync();