
using ThesisExperiment.Models;

namespace ThesisExperiment.Commands
{
    public class RunRecord
    {
        public string RunId { get; set; }
        public DateTime TimestampStart { get; set; }
        public DateTime TimestampEnd { get; set; }
        public ProjectInfo Project { get; set; }
        public MethodInfo Method { get; set; }
        public ExperimentInfo Experiment { get; set; }
        public ModelInfo Model { get; set; }
        public PromptInfo Prompt { get; set; }
        public ResponseInfo Response { get; set; }
        public BuildResult Build { get; set; }
        public TestResult Test { get; set; }
        public CoverageResult Coverage { get; set; }
        public MutationResult Mutation { get; set; }
        public FlakinessResult Flakiness { get; set; }
        public OutcomeInfo Outcome { get; set; }
        public ErrorInfo Error { get; set; }
    }
}
