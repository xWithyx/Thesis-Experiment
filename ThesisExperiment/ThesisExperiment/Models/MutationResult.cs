using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Commands
{
    /// <summary>Method-level mutation testing metrics.</summary>
    public class MutationResult
    {
        public string Tool { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string ScopedTo { get; set; } = string.Empty;
        public int MutantsKilled { get; set; }
        public int MutantsSurvived { get; set; }
        public int MutantsTotal { get; set; }
        public double? MutationScore { get; set; }
    }
}
