using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

namespace ThesisExperiment.Commands
{
    public class DotnetTestService
    {
        private const int MaxOutputLength = 10_000;

        /// <summary>
        /// Run dotnet test with XPlat Code Coverage collection.
        /// Returns a TestResult and the path to the coverage results directory.
        /// </summary>
        public async Task<(TestResult TestResult, string CoverageDir)> RunTestsWithCoverageAsync(
            string workingDirectory)
        {
            var sw = Stopwatch.StartNew();

            var coverageDir = Path.Combine(workingDirectory, "coverage-results");

            // Clean previous coverage results
            if (Directory.Exists(coverageDir))
                Directory.Delete(coverageDir, recursive: true);

            var result = await Cli.Wrap("dotnet")
                .WithArguments(new[]
                {
                    "test",
                    "--no-build",
                    "--collect:XPlat Code Coverage",
                    $"--results-directory:{coverageDir}"
                })
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            sw.Stop();

            var testResult = new TestResult
            {
                Command = "dotnet test --no-build --collect:\"XPlat Code Coverage\"",
                ExitCode = result.ExitCode,
                Stdout = Truncate(result.StandardOutput),
                Stderr = Truncate(result.StandardError),
                DurationSeconds = sw.Elapsed.TotalSeconds
            };

            return (testResult, coverageDir);
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxOutputLength)
                return text;
            return text[..MaxOutputLength] + $"\n... [truncated, {text.Length} total chars]";
        }
    }
}
