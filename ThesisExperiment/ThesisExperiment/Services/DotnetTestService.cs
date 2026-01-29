using System.Diagnostics;
using CliWrap;
using CliWrap.Buffered;

using ThesisExperiment.Models;

namespace ThesisExperiment.Services
{
    /// <summary>Runs dotnet test with optional coverage and filtering.</summary>
    public class DotnetTestService
    {
        private const int MaxOutputLength = 10_000;
        private readonly string? _runsettingsPath;

        public DotnetTestService()
        {
            // Find test.runsettings relative to the executable location
            var appDir = AppContext.BaseDirectory;
            var runsettings = Path.Combine(appDir, "test.runsettings");

            // Also check project directory (for development)
            if (!File.Exists(runsettings))
            {
                var projectDir = Path.GetDirectoryName(
                    Path.GetDirectoryName(
                        Path.GetDirectoryName(
                            Path.GetDirectoryName(appDir))));
                if (projectDir != null)
                {
                    runsettings = Path.Combine(projectDir, "test.runsettings");
                }
            }

            _runsettingsPath = File.Exists(runsettings) ? runsettings : null;
            if (_runsettingsPath != null)
            {
                Console.WriteLine($"Using runsettings: {_runsettingsPath}");
            }
        }

        /// <summary>Runs tests with XPlat Code Coverage collection.</summary>
        public async Task<(TestResult TestResult, string CoverageDir)> RunTestsWithCoverageAsync(
            string workingDirectory, string? filter = null)
        {
            var sw = Stopwatch.StartNew();

            // Use absolute path to avoid dotnet test resolving relative paths incorrectly
            var absoluteWorkingDir = Path.GetFullPath(workingDirectory);
            var coverageDir = Path.Combine(absoluteWorkingDir, "coverage-results");

            if (Directory.Exists(coverageDir))
                Directory.Delete(coverageDir, recursive: true);

            var args = new List<string>
            {
                "test",
                "--no-build",
                "--collect:XPlat Code Coverage",
                $"--results-directory:{coverageDir}"
            };

            if (_runsettingsPath != null)
            {
                args.Add("--settings");
                args.Add(_runsettingsPath);
            }

            if (!string.IsNullOrEmpty(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var result = await Cli.Wrap("dotnet")
                .WithArguments(args)
                .WithWorkingDirectory(absoluteWorkingDir)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            sw.Stop();

            var filterSuffix = filter != null ? $" --filter \"{filter}\"" : "";
            var settingsSuffix = _runsettingsPath != null ? " --settings test.runsettings" : "";
            var testResult = new TestResult
            {
                Command = $"dotnet test --no-build --collect:\"XPlat Code Coverage\"{settingsSuffix}{filterSuffix}",
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

            // Use absolute path for consistency
            var absoluteWorkingDir = Path.GetFullPath(workingDirectory);

            var args = new List<string> { "test", "--no-build" };

            if (_runsettingsPath != null)
            {
                args.Add("--settings");
                args.Add(_runsettingsPath);
            }

            if (!string.IsNullOrEmpty(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var result = await Cli.Wrap("dotnet")
                .WithArguments(args)
                .WithWorkingDirectory(absoluteWorkingDir)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            sw.Stop();

            var filterSuffix = filter != null ? $" --filter \"{filter}\"" : "";
            var settingsSuffix = _runsettingsPath != null ? " --settings test.runsettings" : "";
            return new TestResult
            {
                Command = $"dotnet test --no-build{settingsSuffix}{filterSuffix}",
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
