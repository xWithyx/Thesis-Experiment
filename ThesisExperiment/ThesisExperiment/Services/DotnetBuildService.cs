using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace ThesisExperiment.Commands
{
    /// <summary>Runs dotnet restore + build.</summary>
    public class DotnetBuildService
    {
        private const int MaxOutputLength = 10_000;

        /// <summary>Restores and builds the project.</summary>
        public async Task<BuildResult> BuildAsync(string workingDirectory)
        {
            var sw = Stopwatch.StartNew();

            var restoreResult = await Cli.Wrap("dotnet")
                .WithArguments("restore")
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (restoreResult.ExitCode != 0)
            {
                sw.Stop();
                return new BuildResult
                {
                    Command = "dotnet restore",
                    ExitCode = restoreResult.ExitCode,
                    Stdout = Truncate(restoreResult.StandardOutput),
                    Stderr = Truncate(restoreResult.StandardError),
                    DurationSeconds = sw.Elapsed.TotalSeconds
                };
            }

            var buildResult = await Cli.Wrap("dotnet")
                .WithArguments("build --no-restore")
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            sw.Stop();
            return new BuildResult
            {
                Command = "dotnet restore && dotnet build --no-restore",
                ExitCode = buildResult.ExitCode,
                Stdout = Truncate(buildResult.StandardOutput),
                Stderr = Truncate(buildResult.StandardError),
                DurationSeconds = sw.Elapsed.TotalSeconds
            };
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxOutputLength)
                return text;
            return text[..MaxOutputLength] + $"\n... [truncated, {text.Length} total chars]";
        }
    }
}
