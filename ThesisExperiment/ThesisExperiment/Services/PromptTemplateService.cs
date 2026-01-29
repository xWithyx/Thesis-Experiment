using System.Text.RegularExpressions;

namespace ThesisExperiment.Services
{
    /// <summary>System and user message pair for LLM prompting.</summary>
    public class PromptPair
    {
        public string SystemMessage { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
    }

    /// <summary>Loads and fills prompt templates from disk.</summary>
    public class PromptTemplateService
    {
        private static readonly string SingleShotTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "prompts", "v1.1", "single_shot.txt");
        private static readonly string RepairTemplatePath =
            Path.Combine(AppContext.BaseDirectory, "prompts", "v1.1", "repair_attempt.txt");

        public string TemplateVersion => "v1.1";

        public string SingleShotTemplateFile => "prompts/v1.1/single_shot.txt";
        public string RepairTemplateFile => "prompts/v1.1/repair_attempt.txt";

        /// <summary>Builds a single-shot test generation prompt.</summary>
        public PromptPair BuildSingleShotPrompt(
            string testFramework,
            string testAttribute,
            string namespaceName,
            string className,
            string signature,
            string methodBody,
            string classContext)
        {
            var template = File.ReadAllText(SingleShotTemplatePath);
            var filled = ReplacePlaceholders(template, testFramework, testAttribute,
                namespaceName, className, signature, methodBody, classContext);

            return SplitSystemUser(filled);
        }

        /// <summary>Builds a repair-attempt prompt with error feedback.</summary>
        public PromptPair BuildRepairPrompt(
            string testFramework,
            string testAttribute,
            string namespaceName,
            string className,
            string signature,
            string methodBody,
            string previousTest,
            string errorMessage)
        {
            var template = File.ReadAllText(RepairTemplatePath);

            var filled = template
                .Replace("{{TEST_FRAMEWORK}}", testFramework)
                .Replace("{{TEST_ATTRIBUTE}}", testAttribute)
                .Replace("{{NAMESPACE}}", namespaceName)
                .Replace("{{CLASS_NAME}}", className)
                .Replace("{{SIGNATURE}}", signature)
                .Replace("{{METHOD_BODY}}", methodBody)
                .Replace("{{PREVIOUS_TEST}}", previousTest)
                .Replace("{{ERROR_MESSAGE}}", errorMessage);

            return SplitSystemUser(filled);
        }

        private static string ReplacePlaceholders(
            string template,
            string testFramework,
            string testAttribute,
            string namespaceName,
            string className,
            string signature,
            string methodBody,
            string classContext)
        {
            return template
                .Replace("{{TEST_FRAMEWORK}}", testFramework)
                .Replace("{{TEST_ATTRIBUTE}}", testAttribute)
                .Replace("{{NAMESPACE}}", namespaceName)
                .Replace("{{CLASS_NAME}}", className)
                .Replace("{{SIGNATURE}}", signature)
                .Replace("{{METHOD_BODY}}", methodBody)
                .Replace("{{CLASS_CONTEXT}}", classContext);
        }

        private static PromptPair SplitSystemUser(string filledTemplate)
        {
            var match = Regex.Match(
                filledTemplate,
                @"\ASystem:\s*\r?\n(?<sys>.*?)(?:\r?\n)+User:\s*\r?\n(?<usr>.*)\z",
                RegexOptions.Singleline);

            if (!match.Success)
            {
                return new PromptPair
                {
                    SystemMessage = string.Empty,
                    UserMessage = filledTemplate.Trim()
                };
            }

            return new PromptPair
            {
                SystemMessage = match.Groups["sys"].Value.Trim(),
                UserMessage = match.Groups["usr"].Value.Trim()
            };
        }
    }
}
