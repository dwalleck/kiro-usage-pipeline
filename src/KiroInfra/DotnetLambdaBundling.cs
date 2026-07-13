using System;
using System.Diagnostics;
using Amazon.CDK;

namespace KiroInfra
{
    // Local (Docker-free) CDK asset bundling for a .NET Lambda: runs `dotnet publish`
    // on the host into the CDK-provided staging directory, which CDK then zips as the
    // function asset. Returning true skips the Docker fallback; a publish failure throws
    // so the error surfaces at synth rather than silently falling back.
    internal sealed class DotnetLambdaBundling : ILocalBundling
    {
        private readonly string _projectPath;
        private readonly string _runtime;

        public DotnetLambdaBundling(string projectPath, string runtime)
        {
            _projectPath = projectPath;
            _runtime = runtime;
        }

        public bool TryBundle(string outputDir, IBundlingOptions options)
        {
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            psi.ArgumentList.Add("publish");
            psi.ArgumentList.Add(_projectPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(_runtime);
            psi.ArgumentList.Add("--no-self-contained");
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputDir);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start 'dotnet publish' for Lambda bundling.");
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'dotnet publish' failed (exit {process.ExitCode}) bundling {_projectPath}.");
            }

            return true;
        }
    }
}
