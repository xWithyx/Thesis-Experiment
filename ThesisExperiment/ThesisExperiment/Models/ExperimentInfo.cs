using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Experiment variant and attempt metadata.</summary>
    public class ExperimentInfo
    {
        public string Variant { get; set; } = string.Empty;
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }
        public string PromptVersion { get; set; } = string.Empty;
        public string PromptFile { get; set; } = string.Empty;
    }
}
