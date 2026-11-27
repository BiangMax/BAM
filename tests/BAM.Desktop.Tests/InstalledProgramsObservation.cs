using System.IO;
using System.Windows.Media.Imaging;
using BAM.Adapters;
using BAM.Core;
using BAM.Infrastructure;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class InstalledProgramsObservation(ITestOutputHelper output)
{
    public static bool Requested => Environment.GetEnvironmentVariable("BAM_PHASE3_OBSERVE") == "1";

    [Fact(SkipUnless = nameof(Requested), Skip = "Set BAM_PHASE3_OBSERVE=1 on the approved machine for read-only discovery.")]
    public async Task ObservesKnownInstallationsWithoutInvokingControl()
    {
        var token = TestContext.Current.CancellationToken;
        var catalog = new InstallationCatalog(new WindowsProgramFiles());
        var known = catalog.Discover(token);
        var dir = Directory.CreateTempSubdirectory("BamObserve-");
        try
        {
            var config = new LocalProgramStore(dir.FullName);
            await config.SaveAsync(known, [], token);
            using var platform = new WindowsProgramPlatform();
            using var monitor = new ProgramMonitor(catalog, platform, config, new LocalProgramEventStore(dir.FullName), TimeProvider.System);
            await monitor.InitializeAsync(token);
            await monitor.SampleAsync(true, token);
            await Task.Delay(1100, token);
            var result = await monitor.SampleAsync(true, token);
            foreach (var entry in result.Programs)
            {
                string icon = "fallback";
                if (entry.Metadata.Icon is { } iconBytes)
                {
                    using var stream = new MemoryStream(iconBytes);
                    var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    icon = $"{image.PixelWidth}x{image.PixelHeight}";
                }
                output.WriteLine($"{entry.Installation.Kind}: {entry.Metadata.VersionDetail} | icon={icon} | " +
                    $"entry={(entry.Installation.Entry.IsBatch ? "batch" : "exe")} | state={entry.Process} | " +
                    $"CPU={entry.Cpu:0.00} | WS={entry.PrivateWorkingSetMiB:0.0} | uptime={entry.Uptime} | " +
                    $"window={entry.Worker?.Window?.State.ToString() ?? "unavailable"} | " +
                    $"window permission={entry.Worker?.WindowControlAllowed} | issue={entry.Issue}");
            }
            Assert.Contains(result.Programs, entry => entry.Installation.Kind == ProgramKind.MaaEnd);
            Assert.Contains(result.Programs, entry => entry.Installation.Kind == ProgramKind.Maa);
            Assert.Contains(result.Programs, entry => entry.Installation.Kind == ProgramKind.BBchannel);
        }
        finally { dir.Delete(recursive: true); }
    }
}
