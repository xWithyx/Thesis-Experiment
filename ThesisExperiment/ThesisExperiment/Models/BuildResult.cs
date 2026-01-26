using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    public class BuildResult
    {
        public string Command { get; set; }
        public int ExitCode { get; set; }
        public string Stdout { get; set; }
        public string Stderr { get; set; }
        public double DurationSeconds { get; set; }
    }
}
