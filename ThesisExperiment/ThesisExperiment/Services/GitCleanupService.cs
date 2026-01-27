using CliWrap;
using CliWrap.Buffered;

namespace ThesisExperiment.Commands
{
    public class GitCleanupService
    {
        /// <summary>
        /// Ensure repo is cloned and checked out to the exact frozen commit.
        /// If the clone is shallow, unshallow it first so the commit is available.
        /// Returns the local path to the repo working directory.
        /// </summary>
        public async Task<string> EnsureRepoAtCommitAsync(
            string repoUrl, string clonePath, string commitHash)
        {
            if (!Directory.Exists(clonePath))
            {
                Console.WriteLine($"  Cloning {repoUrl} to {clonePath}...");
                var cloneUrl = repoUrl.EndsWith(".git") ? repoUrl : repoUrl + ".git";

                var parentDir = Path.GetDirectoryName(clonePath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    Directory.CreateDirectory(parentDir);

                var result = await Cli.Wrap("git")
                    .WithArguments($"clone {cloneUrl} {clonePath}")
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (result.ExitCode != 0)
                    throw new Exception($"Git clone failed: {result.StandardError}");
            }

            // Check if shallow clone (Step 1 used --depth 1)
            var isShallow = await RunGitAsync(clonePath, "rev-parse --is-shallow-repository");
            if (isShallow.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Unshallowing clone...");
                var unshallow = await Cli.Wrap("git")
                    .WithArguments("fetch --unshallow")
                    .WithWorkingDirectory(clonePath)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (unshallow.ExitCode != 0)
                    Console.WriteLine($"  WARNING: git fetch --unshallow failed: {unshallow.StandardError}");
            }

            // Checkout the frozen commit
            Console.WriteLine($"  Checking out commit {commitHash}...");
            var checkout = await Cli.Wrap("git")
                .WithArguments($"checkout {commitHash}")
                .WithWorkingDirectory(clonePath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (checkout.ExitCode != 0)
                throw new Exception($"Git checkout failed: {checkout.StandardError}");

            // Ensure clean state
            await Cli.Wrap("git")
                .WithArguments($"reset --hard {commitHash}")
                .WithWorkingDirectory(clonePath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            await Cli.Wrap("git")
                .WithArguments("clean -fd")
                .WithWorkingDirectory(clonePath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return clonePath;
        }

        private static async Task<string> RunGitAsync(string workingDirectory, string arguments)
        {
            var result = await Cli.Wrap("git")
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return result.StandardOutput;
        }
    }
}
