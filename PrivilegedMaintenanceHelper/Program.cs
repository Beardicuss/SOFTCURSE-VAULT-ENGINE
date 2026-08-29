using System.Diagnostics;

namespace SoftcurseVaultCleaner.PrivilegedMaintenance;

internal static class Program
{
    private const string ComponentCleanupCommand = "component-cleanup";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !string.Equals(args[0], ComponentCleanupCommand, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Unsupported maintenance command.");
            return 64;
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string dismPath = Path.Combine(windowsDirectory, "System32", "dism.exe");
        if (!File.Exists(dismPath))
        {
            Console.Error.WriteLine("DISM was not found in the Windows system directory.");
            return 69;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dismPath,
                Arguments = "/Online /Cleanup-Image /StartComponentCleanup",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = windowsDirectory
            }
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) Console.Out.WriteLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) Console.Error.WriteLine(eventArgs.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Console.Error.WriteLine("Component cleanup exceeded the 30-minute safety timeout.");
            return 70;
        }
    }
}
