using System.ClientModel;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace ThesisExperiment.Services
{
    /// <summary>Response data from an OpenAI API call.</summary>
    public class OpenAiResult
    {
        public string RawText { get; set; } = string.Empty;
        public string ExtractedCode { get; set; } = string.Empty;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public string FinishReason { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
    }

    /// <summary>Calls the OpenAI chat completions API to generate test code.</summary>
    public class OpenAiService
    {
        private readonly ChatClient _chatClient;

        public OpenAiService()
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new InvalidOperationException(
                    "Environment variable OPENAI_API_KEY is not set. " +
                    "Set it before running: set OPENAI_API_KEY=sk-...");

            _chatClient = new ChatClient(model: "gpt-3.5-turbo", apiKey: apiKey);
        }

        /// <summary>Sends system+user messages to GPT and returns the result.</summary>
        public async Task<OpenAiResult> GenerateTestAsync(
            string systemMessage, string userMessage)
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemMessage),
                new UserChatMessage(userMessage)
            };

            var options = new ChatCompletionOptions
            {
                Temperature = 0.2f,
                MaxOutputTokenCount = 2048
            };

            ClientResult<ChatCompletion> result =
                await _chatClient.CompleteChatAsync(messages, options);

            ChatCompletion completion = result.Value;

            var rawText = completion.Content.Count > 0
                ? completion.Content[0].Text ?? string.Empty
                : string.Empty;

            var requestId = string.Empty;
            try
            {
                var rawResponse = result.GetRawResponse();
                if (rawResponse.Headers.TryGetValue("x-request-id", out var reqId))
                    requestId = reqId ?? string.Empty;
            }
            catch
            {
            }

            return new OpenAiResult
            {
                RawText = rawText,
                ExtractedCode = ExtractCodeBlock(rawText),
                PromptTokens = completion.Usage.InputTokenCount,
                CompletionTokens = completion.Usage.OutputTokenCount,
                TotalTokens = completion.Usage.TotalTokenCount,
                FinishReason = completion.FinishReason.ToString(),
                ModelId = completion.Model ?? "unknown",
                RequestId = requestId
            };
        }

        private static string ExtractCodeBlock(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var match = Regex.Match(text, @"```(?:csharp|cs)?\s*\n(.*?)```",
                RegexOptions.Singleline);

            return match.Success ? match.Groups[1].Value.Trim() : text.Trim();
        }
    }
}
