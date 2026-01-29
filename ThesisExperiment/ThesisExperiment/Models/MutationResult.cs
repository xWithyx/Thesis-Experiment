using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Method-level mutation testing metrics.</summary>
    public class MutationResult
    {
        /// <summary>
        /// Mutation testing status:
        /// - "available": Stryker ran successfully
        /// - "timeout": Stryker timed out for this file
        /// - "skipped_budget": Skipped due to budget (2+ consecutive timeouts for this repo)
        /// - "error": Stryker failed with an error
        /// </summary>
        public string Status { get; set; } = "available";
        public string Tool { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string ScopedTo { get; set; } = string.Empty;
        public int MutantsKilled { get; set; }
        public int MutantsSurvived { get; set; }
        public int MutantsTotal { get; set; }
        public double? MutationScore { get; set; }
        public string? Note { get; set; }
    }
}
