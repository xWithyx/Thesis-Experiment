using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    /// <summary>LLM response data and token usage.</summary>
    public class ResponseInfo
    {
        public string RawText { get; set; } = string.Empty;
        public string ExtractedCode { get; set; } = string.Empty;
        public int TokensUsed { get; set; }           
        public int PromptTokens { get; set; }         
        public int CompletionTokens { get; set; }      
        public string FinishReason { get; set; } = string.Empty;
    }
}
