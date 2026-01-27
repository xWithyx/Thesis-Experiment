using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    /// <summary>Captured output from a dotnet test invocation.</summary>
    public class TestResult
    {
        public string Command { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string Stdout { get; set; } = string.Empty;
        public string Stderr { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
    }
}
