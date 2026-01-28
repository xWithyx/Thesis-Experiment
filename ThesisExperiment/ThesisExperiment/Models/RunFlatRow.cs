namespace ThesisExperiment.Models
{
    /// <summary>Flat representation of a RunRecord for CSV export (one row per run).</summary>
    public class RunFlatRow
    {
        // Experiment
        public string Variant { get; set; } = string.Empty;
        public int AttemptNumber { get; set; }
        public int MaxAttempts { get; set; }

        // Project
        public string ProjectName { get; set; } = string.Empty;
        public string RepoUrl { get; set; } = string.Empty;
        public int Stars { get; set; }
        public string CommitHash { get; set; } = string.Empty;

        // Method
        public string MethodIdentifier { get; set; } = string.Empty;
        public string ContainingClass { get; set; } = string.Empty;
        public string MethodSignature { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int LineStart { get; set; }
        public int LineEnd { get; set; }

        // Outcome
        public string FinalStatus { get; set; } = string.Empty;
        public string StopReason { get; set; } = string.Empty;
        public bool Gate1BuildPassed { get; set; }
        public bool Gate2TestPassed { get; set; }

        // Build & Test
        public int BuildExitCode { get; set; }
        public int TestExitCode { get; set; }

        // Flakiness
        public string FlakinessClassification { get; set; } = string.Empty;
        public int FlakinessRunsExecuted { get; set; }
        public string FlakinessRunResults { get; set; } = string.Empty;

        // Coverage
        public string CoverageStatus { get; set; } = string.Empty;
        public double? CoverageLinePercent { get; set; }
        public double? CoverageBranchPercent { get; set; }

        // Mutation
        public double? MutationScore { get; set; }
        public int MutantsKilled { get; set; }
        public int MutantsSurvived { get; set; }
        public int MutantsTotal { get; set; }

        // Model
        public string ModelProvider { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }
        public string RequestId { get; set; } = string.Empty;

        // Tokens
        public int TokensUsed { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public string FinishReason { get; set; } = string.Empty;

        // Prompt metadata
        public string PromptVersion { get; set; } = string.Empty;
        public string PromptFile { get; set; } = string.Empty;
        public string TemplateVersion { get; set; } = string.Empty;
        public string TemplateFile { get; set; } = string.Empty;

        // Error (original from RunRecord)
        public string ErrorCategory { get; set; } = string.Empty;
        public string ErrorSubcategory { get; set; } = string.Empty;

        // Error labels (assigned by LabelErrorsCommand)
        public string ErrorCategoryLabeled { get; set; } = string.Empty;
        public string ErrorSubcategoryLabeled { get; set; } = string.Empty;
        public string LabelVersion { get; set; } = string.Empty;
        public string PrimaryErrorCode { get; set; } = string.Empty;

        // Timestamps
        public DateTime TimestampStart { get; set; }
        public DateTime TimestampEnd { get; set; }
        public string RunId { get; set; } = string.Empty;
    }
}
