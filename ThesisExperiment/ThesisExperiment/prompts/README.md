# Prompt Templates

## Versioning Rules

- `prompts/v1.0/` is **frozen** after initial commit. Do not modify these files.
- Any changes require a new version folder: `prompts/v1.1/`, `prompts/v2.0/`, etc.
- Steps 5 and 6 log `prompt_version = "v1.0"` in every RunRecord JSON.

## Placeholders

| Placeholder | Description | Filled by |
|---|---|---|
| `{{TEST_FRAMEWORK}}` | Test framework name (xUnit, NUnit, MSTest) | Step 5/6 auto-detect |
| `{{TEST_ATTRIBUTE}}` | Test method attribute (Fact, Test, TestMethod) | Step 5/6 auto-detect |
| `{{NAMESPACE}}` | Namespace of the focal class | method_list_all.csv |
| `{{CLASS_NAME}}` | Name of the focal class | method_list_all.csv |
| `{{SIGNATURE}}` | Full method signature | method_list_all.csv |
| `{{METHOD_BODY}}` | Source code of the focal method | Extracted from repo |
| `{{CLASS_CONTEXT}}` | Other methods and fields in the class | Extracted from repo |
| `{{PREVIOUS_TEST}}` | Previous test code (repair only) | Step 6 repair loop |
| `{{ERROR_MESSAGE}}` | Build/test error output (repair only) | Step 6 repair loop |

## Files

- `v1.0/single_shot.txt` — Used by Step 5 (Variant B, single-shot generation)
- `v1.0/repair_attempt.txt` — Used by Step 6 (Variant C, repair loop iterations)
