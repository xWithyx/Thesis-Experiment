# Manual Prompt Template Test Notes

Date: 2026-01-27
Template version: v1.0
Test framework used: xUnit (TEST_FRAMEWORK=xUnit, TEST_ATTRIBUTE=Fact)

**Note on method selection:** `method_list_all.csv` is generated at runtime by Step 2
(`sample-methods`) and may not be present in the repo on a fresh clone. For this template
format validation, placeholders were filled using representative methods from the tool
codebase itself. The same placeholder structure (`NAMESPACE`, `CLASS_NAME`, `SIGNATURE`,
`METHOD_BODY`, `CLASS_CONTEXT`) is filled from `method_list_all.csv` and repository source
extraction during the actual experiment run (Steps 5/6).

---

## Method 1: SanitizeForFilename (easy, pure logic)

**Filled single_shot.txt prompt:**

```
System:
You are a unit test generation assistant for C# using xUnit.
Generate exactly one unit test method that tests the given focal method.
Return only the test code inside a single code block.
Use the Fact attribute.
Do not include any explanations outside the code block.

Constraints:
- The test must be deterministic (no randomness, no time dependence).
- Do not use network, environment variables, or external services.
- File system access is allowed only via temporary directories/files created and deleted within the test.
- Prefer simple Arrange-Act-Assert structure.
- If setup is required, include minimal setup inside the test (or a test class constructor if required by framework).
- Assume the project already has the needed test framework references installed.

User:
Generate a unit test for the following method.

Namespace: ThesisExperiment.Commands
Class: JsonLogger
Method signature: private static string SanitizeForFilename(string input)

Method body:
var invalid = Path.GetInvalidFileNameChars();
var sb = new StringBuilder(input.Length);
foreach (var c in input)
    sb.Append(invalid.Contains(c) ? '_' : c);

return sb.ToString()
    .Replace('(', '_')
    .Replace(')', '_')
    .Replace(',', '_')
    .Replace(' ', '_');

Class context (other methods and fields):
public void WriteRunRecord(RunRecord record, string filePath) { ... }
public static string GetOutputPath(string runsDir, string projectName, string identifier, string variant) { ... }
```

**Checklist:**
- [x] All placeholders filled correctly
- [x] System/User sections clearly separated
- [x] No ambiguity in method signature or body
- [x] Note: method is `private static` — model must use reflection or test via public caller `GetOutputPath`

---

## Method 2: EndsWithOnBoundary (static helper with logic)

**Filled single_shot.txt prompt:**

```
System:
You are a unit test generation assistant for C# using xUnit.
Generate exactly one unit test method that tests the given focal method.
Return only the test code inside a single code block.
Use the Fact attribute.
Do not include any explanations outside the code block.

Constraints:
- The test must be deterministic (no randomness, no time dependence).
- Do not use network, environment variables, or external services.
- File system access is allowed only via temporary directories/files created and deleted within the test.
- Prefer simple Arrange-Act-Assert structure.
- If setup is required, include minimal setup inside the test (or a test class constructor if required by framework).
- Assume the project already has the needed test framework references installed.

User:
Generate a unit test for the following method.

Namespace: ThesisExperiment.Commands
Class: CoverletService
Method signature: private static bool EndsWithOnBoundary(string haystack, string needle)

Method body:
if (!haystack.EndsWith(needle, StringComparison.OrdinalIgnoreCase))
    return false;
if (haystack.Length == needle.Length)
    return true;
char preceding = haystack[haystack.Length - needle.Length - 1];
return preceding == '/' || preceding == '\\';

Class context (other methods and fields):
public List<string> FindCoberturaFiles(string coverageResultsDir) { ... }
public CoverageResult ParseMethodCoverage(List<string> coberturaFiles, string methodFilePath, int lineStart, int lineEnd, string repoRootPath) { ... }
```

**Checklist:**
- [x] All placeholders filled correctly
- [x] System/User sections clearly separated
- [x] Method body shows clear branching logic (3 paths: no suffix, exact match, boundary check)
- [x] Note: method is `private static` — model must use reflection or test indirectly via ParseMethodCoverage

---

## Method 3: FindCoberturaFiles (IO-dependent edge case)

**Filled single_shot.txt prompt:**

```
System:
You are a unit test generation assistant for C# using xUnit.
Generate exactly one unit test method that tests the given focal method.
Return only the test code inside a single code block.
Use the Fact attribute.
Do not include any explanations outside the code block.

Constraints:
- The test must be deterministic (no randomness, no time dependence).
- Do not use network, environment variables, or external services.
- File system access is allowed only via temporary directories/files created and deleted within the test.
- Prefer simple Arrange-Act-Assert structure.
- If setup is required, include minimal setup inside the test (or a test class constructor if required by framework).
- Assume the project already has the needed test framework references installed.

User:
Generate a unit test for the following method.

Namespace: ThesisExperiment.Commands
Class: CoverletService
Method signature: public List<string> FindCoberturaFiles(string coverageResultsDir)

Method body:
if (!Directory.Exists(coverageResultsDir))
    return new List<string>();

return Directory.GetFiles(coverageResultsDir, "coverage.cobertura.xml",
    SearchOption.AllDirectories).ToList();

Class context (other methods and fields):
public CoverageResult ParseMethodCoverage(List<string> coberturaFiles, string methodFilePath, int lineStart, int lineEnd, string repoRootPath) { ... }
private static bool EndsWithOnBoundary(string haystack, string needle) { ... }
```

**Checklist:**
- [x] All placeholders filled correctly
- [x] System/User sections clearly separated
- [x] Edge case: method uses file system directly (Directory.Exists, Directory.GetFiles)
- [x] Expected model behavior: can use temp directories (allowed by constraint) to test both branches, or test only the non-existent directory branch (returns empty list) without IO

---

## Repair Template Test (Method 1 with error)

**Filled repair_attempt.txt prompt:**

```
System:
You are a unit test generation assistant for C# using xUnit.
The previous test attempt failed. Fix the error and return the corrected test.
Return only the test code inside a single code block.
Use the Fact attribute.
Do not include explanations outside the code block.

Constraints:
- Keep the test focused on the same focal method.
- Fix compile errors first. If it compiles, fix failing assertions/setup.
- The test must be deterministic (no randomness, no time dependence).

User:
Original method:

Namespace: ThesisExperiment.Commands
Class: JsonLogger
Method signature: private static string SanitizeForFilename(string input)

Method body:
var invalid = Path.GetInvalidFileNameChars();
var sb = new StringBuilder(input.Length);
foreach (var c in input)
    sb.Append(invalid.Contains(c) ? '_' : c);

return sb.ToString()
    .Replace('(', '_')
    .Replace(')', '_')
    .Replace(',', '_')
    .Replace(' ', '_');

Previous test that failed:
[Fact]
public void SanitizeForFilename_ReplacesInvalidChars()
{
    var result = JsonLogger.SanitizeForFilename("test (1, 2)");
    Assert.Equal("test__1__2_", result);
}

Error message:
error CS0122: 'JsonLogger.SanitizeForFilename(string)' is inaccessible due to its protection level

Fix the test and return only the corrected code.
```

**Checklist:**
- [x] All placeholders filled (including PREVIOUS_TEST and ERROR_MESSAGE)
- [x] Error message is realistic (CS0122 for private method)
- [x] Model should respond with reflection-based access or test via public GetOutputPath
- [x] Template clearly separates original method, failed test, and error

---

## Overall Assessment

| Check | Result |
|---|---|
| All placeholders fillable from CSV + source extraction | Pass |
| System/User prompt sections unambiguous | Pass |
| Constraints are clear and framework-neutral | Pass |
| Repair template includes all necessary context | Pass |
| Templates work for both public and private methods | Pass (model must adapt approach) |
| No hardcoded framework references in template | Pass (uses {{TEST_FRAMEWORK}} / {{TEST_ATTRIBUTE}}) |

**Conclusion:** Templates are stable, well-structured, and ready for automated use in Steps 5/6.
The placeholder set covers the information available from `method_list_all.csv` and source code extraction.
Framework detection (xUnit/NUnit/MSTest) will be handled by Step 5/6 at runtime.
