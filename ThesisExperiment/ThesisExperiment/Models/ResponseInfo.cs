using System;
using System.Collections.Generic;
using System.Text;

namespace ThesisExperiment.Models
{
    public class ResponseInfo
    {
        public string RawText { get; set; }           
        public string ExtractedCode { get; set; }      
        public int TokensUsed { get; set; }           
        public int PromptTokens { get; set; }         
        public int CompletionTokens { get; set; }      
        public string FinishReason { get; set; }
    }
}
