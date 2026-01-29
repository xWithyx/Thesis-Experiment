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
        /// <summary>Repair policy version for Variant C (e.g. "RP-1.0").</summary>
        public string RepairPolicyVersion { get; set; } = string.Empty;
        /// <summary>RunId of the source Variant B record (for traceability).</summary>
        public string SourceVariantBRunId { get; set; } = string.Empty;
    }
}
