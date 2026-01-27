using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace ThesisExperiment.Commands
{
    /// <summary>Runs dotnet test with optional coverage and filtering.</summary>
    public class DotnetTestService
    {
        private const int MaxOutputLength = 10_000;

        /// <summary>Runs tests with XPlat Code Coverage collection.</summary>
        public async Task<(TestResult TestResult, string CoverageDir)> RunTestsWithCoverageAsync(
            string workingDirectory, string? filter = null)
        {
            var sw = Stopwatch.StartNew();

            var coverageDir = Path.Combine(workingDirectory, "coverage-results");

            if (Directory.Exists(coverageDir))
                Directory.Delete(coverageDir, recursive: true);

            var args = new List<string>
            {
                "test",
                "--no-build",
                "--collect:XPlat Code Coverage",
                $"--results-directory:{coverageDir}"
            };

            if (!string.IsNullOrEmpty(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var result = await Cli.Wrap("dotnet")
                .WithArguments(args)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            sw.Stop();

            var filterSuffix = filter != null ? $" --filter \"{filter}\"" : "";
            var testResult = new TestResult
            {
                Command = $"dotnet test --no-build --collect:\"XPlat Code Coverage\"{filterSuffix}",
                ExitCode = result.ExitCode,
                Stdout = Truncate(result.StandardOutput),
                Stderr = Truncate(result.StandardError),
                DurationSeconds = sw.Elapsed.TotalSeconds
            };

            return (testResult, coverageDir);
        }

        /// <summary>Runs tests without coverage (for flakiness reruns).</summary>
        public async Task<TestResult> RunTestsOnlyAsync(
            string workingDirectory, string? filter = null)
        {
            var sw = Stopwatch.StartNew();

            var args = new List<string> { "test", "--no-build" };

            if (!string.IsNullOrEmpty(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var result = await Cli.Wrap("dotnet")
                .WithArguments(args)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            sw.Stop();

            var filterSuffix = filter != null ? $" --filter \"{filter}\"" : "";
            return new TestResult
            {
                Command = $"dotnet test --no-build{filterSuffix}",
                ExitCode = result.ExitCode,
                Stdout = Truncate(result.StandardOutput),
                Stderr = Truncate(result.StandardError),
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
