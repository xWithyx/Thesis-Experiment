namespace ThesisExperiment.Models
{
    /// <summary>Top-level experiment record written as JSON per method.</summary>
    public class RunRecord
    {
        public string RunId { get; set; } = string.Empty;
        public DateTime TimestampStart { get; set; }
        public DateTime TimestampEnd { get; set; }
        public ProjectInfo Project { get; set; } = new ProjectInfo();
        public MethodInfo Method { get; set; } = new MethodInfo();
        public ExperimentInfo Experiment { get; set; } = new ExperimentInfo();
        public ModelInfo Model { get; set; } = new ModelInfo();
        public PromptInfo Prompt { get; set; } = new PromptInfo();
        public ResponseInfo Response { get; set; } = new ResponseInfo();
        public BuildResult Build { get; set; } = new BuildResult();
        public TestResult Test { get; set; } = new TestResult();
        public CoverageResult Coverage { get; set; } = new CoverageResult();
        public MutationResult Mutation { get; set; } = new MutationResult();
        public FlakinessResult Flakiness { get; set; } = new FlakinessResult();
        public OutcomeInfo Outcome { get; set; } = new OutcomeInfo();
        public ErrorInfo Error { get; set; } = new ErrorInfo();
    }
}
