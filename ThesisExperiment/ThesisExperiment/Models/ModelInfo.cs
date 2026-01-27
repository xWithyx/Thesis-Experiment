namespace ThesisExperiment.Commands
{
    /// <summary>LLM provider and configuration details.</summary>
    public class ModelInfo
    {
        public string Provider { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public double Temperature { get; set; }     
        public int MaxTokens { get; set; }          
        public string RequestId { get; set; } = string.Empty;
    }
}