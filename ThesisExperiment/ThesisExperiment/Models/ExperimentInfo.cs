using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class ExperimentInfo
    {
        public string Variant { get; set; }
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }
        public string PromptVersion { get; set; }
        public string PromptFile { get; set; }
    }
}
