using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace ThesisExperiment.Commands
{
    public class DotnetBuildService
    {
        private const int MaxOutputLength = 10_000;

        /// <summary>
        /// Run dotnet restore followed by dotnet build --no-restore.
        /// Returns a populated BuildResult with exit code, stdout, stderr, and duration.
        /// </summary>
        public async Task<BuildResult> BuildAsync(string workingDirectory)
        {
            var sw = Stopwatch.StartNew();

            // Step 1: dotnet restore
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

            // Step 2: dotnet build --no-restore (Debug config for coverage compatibility)
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
