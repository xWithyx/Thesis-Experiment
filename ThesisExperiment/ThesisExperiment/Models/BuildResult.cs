using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Captured output from a dotnet build invocation.</summary>
    public class BuildResult
    {
        public string Command { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string Stdout { get; set; } = string.Empty;
        public string Stderr { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
    }
}
