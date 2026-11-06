using System.Diagnostics;

namespace InvoiceFlow.Evals.Synthetic;

/// <summary>
/// Renders HTML to PDF/PNG with a locally installed Chromium browser (Edge or Chrome) in headless mode.
/// No extra NuGet dependency, and Arabic shaping/RTL work out of the box.
/// </summary>
public sealed class BrowserRenderer
{
    private static readonly string[] KnownPaths =
    [
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        "/usr/bin/microsoft-edge",
        "/usr/bin/google-chrome",
        "/usr/bin/chromium",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    ];

    private readonly string _browser;

    public BrowserRenderer(string? browserPath)
    {
        _browser = browserPath ?? KnownPaths.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("No Edge/Chrome found. Pass --browser <path>.");
    }

    public string BrowserPath => _browser;

    public async Task RenderAsync(string htmlPath, string outputPath, OutputFormat format, CancellationToken ct)
    {
        var profile = Path.Combine(Path.GetTempPath(), "invoiceflow-render-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo(_browser)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        foreach (var arg in new[]
                 {
                     "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--hide-scrollbars", $"--user-data-dir={profile}",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        if (format == OutputFormat.Pdf)
        {
            psi.ArgumentList.Add("--no-pdf-header-footer");
            psi.ArgumentList.Add($"--print-to-pdf={outputPath}");
        }
        else
        {
            // ≈ A4 portrait at 150 dpi-ish: 827×1170 CSS px × 1.5.
            psi.ArgumentList.Add("--window-size=827,1170");
            psi.ArgumentList.Add("--force-device-scale-factor=1.5");
            psi.ArgumentList.Add($"--screenshot={outputPath}");
        }

        psi.ArgumentList.Add(new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri);

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {_browser}.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Headless Chromium can linger for many seconds after writing the file. Wait until the
            // output exists and its size is stable, then stop the browser instead of waiting for it.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            long lastSize = -1;
            var stableSince = DateTime.MaxValue;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;

                if (size > 0 && size == lastSize)
                {
                    if (stableSince == DateTime.MaxValue)
                    {
                        stableSince = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(700))
                    {
                        break;
                    }
                }
                else
                {
                    stableSince = DateTime.MaxValue;
                }

                if (process.HasExited && size == 0)
                {
                    throw new InvalidOperationException($"Browser exited without output for {htmlPath} (exit code {process.ExitCode}).");
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Rendering {htmlPath} took more than 60 s.");
                }

                lastSize = size;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            TryDelete(profile);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Browser may still hold a lock for a moment; temp folder is cleaned by the OS later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
