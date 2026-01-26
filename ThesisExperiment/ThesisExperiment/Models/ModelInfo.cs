namespace ThesisExperiment.Commands
{
    public class ModelInfo
    {
        public string Provider { get; set; }     
        public string ModelId { get; set; }        
        public double Temperature { get; set; }     
        public int MaxTokens { get; set; }          
        public string RequestId { get; set; }
    }
}