using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>Method-level line and branch coverage metrics.</summary>
    public class CoverageResult
    {
        /// <summary>
        /// Coverage collection status:
        /// - "available": Coverage data collected successfully
        /// - "collector_missing": XPlat Code Coverage collector not installed
        /// - "no_report": Collector present but no report generated (test abort, path issue, etc.)
        /// </summary>
        public string Status { get; set; } = "available";
        public string Tool { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public int? LineCovered { get; set; }
        public int? LineTotal { get; set; }
        public double? LinePercent { get; set; }
        public int? BranchCovered { get; set; }
        public int? BranchTotal { get; set; }
        public double? BranchPercent { get; set; }
        public string? Note { get; set; }
    }
}
