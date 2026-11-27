using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BAM.Adapters;
using BAM.Core;
using BAM.Infrastructure;
using Xunit;

namespace BAM.Desktop.Tests;

public sealed class WindowsProgramTests(ITestOutputHelper output)
{
    public static bool InteractiveFixture => Environment.GetEnvironmentVariable("BAM_PHASE3_FIXTURE") == "1";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(SkipUnless = nameof(InteractiveFixture), Skip = "Set BAM_PHASE3_FIXTURE=1 in an interactive Windows session.")]
    public async Task EmulatorSamplerReadsOnlyTheOwnedProcessesAndRejectsReusedIdentity()
    {
        var root = Directory.CreateTempSubdirectory("Bam owned metrics ");
        var executable = Path.Combine(root.FullName, $"BamMetrics{Guid.NewGuid():N}.exe");
        try
        {
            await Compile(executable);
            using var owned = Process.Start(new ProcessStartInfo(executable, "phase3 --instance=9") { WorkingDirectory = root.FullName })!;
            Process? sibling = null;
            try
            {
                sibling = Process.Start(new ProcessStartInfo(executable, "phase3 --index=4") { WorkingDirectory = root.FullName })!;
                using var platform = new WindowsProgramPlatform();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { new WindowsProgramFiles().ResolveImagePath(executable) };
                var workers = await Until(() => Task.FromResult(platform.ScanEmulators(paths, Token)),
                    scan => scan.Complete && scan.Workers.Count == 2 &&
                        scan.Workers.All(item => item.PrivateWorkingSet > 0) &&
                        File.Exists(Path.Combine(root.FullName, "fixture-evidence.txt")));
                var worker = Assert.Single(workers.Workers, item => item.Identity.Pid == owned.Id);
                var other = Assert.Single(workers.Workers, item => item.Identity.Pid == sibling.Id);
                Assert.All(workers.Workers, item =>
                {
                    Assert.NotNull(item.PrivateWorkingSet);
                    Assert.NotNull(item.CpuTime);
                    Assert.False(item.CanTerminate);
                    Assert.Null(item.Window);
                });
                owned.Refresh();
                Assert.InRange(worker.PrivateWorkingSet!.Value, 1L, owned.WorkingSet64 - 1);
                var stale = new ProcessIdentity(worker.Identity.ImagePath, owned.Id, worker.Identity.CreatedAtUtc.AddTicks(-1));
                var arguments = platform.ReadEmulatorArgumentsBatch([worker.Identity, other.Identity, stale], Token);
                Assert.Contains("--instance=9", arguments[worker.Identity]!);
                Assert.Contains("--index=4", arguments[other.Identity]!);
                Assert.False(arguments.ContainsKey(stale));
                Assert.Null(platform.ReadEmulatorArguments(stale, Token));
                output.WriteLine("Verified read-only CPU, private working set below total, two instance arguments and stale identity rejection.");
            }
            finally
            {
                if (sibling is not null)
                {
                    using (sibling)
                    {
                        if (!sibling.HasExited) sibling.Kill();
                        await sibling.WaitForExitAsync(CancellationToken.None);
                    }
                }
                if (!owned.HasExited) owned.Kill();
                await owned.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            try { await Until(() => Task.FromResult(RemoveFixture()), removed => removed); }
            catch (TimeoutException) { output.WriteLine("The fixture directory remained locked after the process exited."); }
        }

        bool RemoveFixture()
        {
            try { root.Delete(recursive: true); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return !root.Exists; }
        }
    }

    [Fact(SkipUnless = nameof(InteractiveFixture), Skip = "Set BAM_PHASE3_FIXTURE=1 in an interactive Windows session.")]
    public async Task FixtureUsesOriginalBatchWorkingDirectoryAndControlsOnlyExactWorker()
    {
        var root = Directory.CreateTempSubdirectory("Bam owned fixture ");
        var own = root.CreateSubdirectory("primary");
        var alternate = root.CreateSubdirectory("alternate");
        var working = root.CreateSubdirectory("working dir");
        var filename = $"BamFixture{Guid.NewGuid():N}.exe";
        var worker = Path.Combine(own.FullName, filename);
        var other = Path.Combine(alternate.FullName, filename);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var platform = new WindowsProgramPlatform();
        try
        {
            await Compile(worker);
            File.Copy(worker, other);
            var files = new WindowsProgramFiles();
            allowed.Add(files.ResolveImagePath(worker));
            allowed.Add(files.ResolveImagePath(other));
            var batch = Path.Combine(own.FullName, "启动 fixture.cmd");
            await File.WriteAllTextAsync(batch, $"@echo off\r\nstart \"\" /b \"%~dp0{filename}\" phase3 %*\r\n", Token);
            var link = Path.Combine(root.FullName, "fixture.lnk");
            WriteLink(link, batch, working.FullName, "\"two words\"", worker);
            var parsed = files.ReadEntry(link);
            Assert.Equal(files.ResolveImagePath(batch), files.ResolveImagePath(parsed.TargetPath), ignoreCase: true);
            Assert.Equal(files.ResolveImagePath(working.FullName), files.ResolveImagePath(parsed.WorkingDirectory), ignoreCase: true);
            Assert.Equal("\"two words\"", parsed.Arguments);
            Assert.NotNull(parsed.IconPath);
            Assert.Equal(files.ResolveImagePath(worker), files.ResolveImagePath(parsed.IconPath), ignoreCase: true);
            Assert.Equal(1, parsed.IconIndex);
            var catalog = new InstallationCatalog(files);
            var program = catalog.Edit(catalog.Import(link), "Fixture", working.FullName, parsed.Arguments, worker);
            var store = new LocalProgramStore(root.FullName);
            await store.SaveAsync([program], [], Token);
            using var monitor = new ProgramMonitor(catalog, platform, store, new LocalProgramEventStore(root.FullName), TimeProvider.System);
            await monitor.InitializeAsync(Token);
            var ready = Assert.Single((await monitor.SampleAsync(true, Token)).Programs);
            program = ready.Installation;
            Assert.Equal(ProcessState.Stopped, ready.Process);
            var updatedWorking = root.CreateSubdirectory("updated working dir");
            WriteLink(link, batch, updatedWorking.FullName, "\"updated words\"", worker);
            Assert.Equal(CommandStatus.Denied,
                (await monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token, ready.Metadata.EffectiveEntry)).Status);
            var updated = Assert.Single((await monitor.SampleAsync(true, Token)).Programs).Metadata.EffectiveEntry;
            Assert.NotNull(updated);
            Assert.Equal(updatedWorking.FullName, updated.WorkingDirectory, ignoreCase: true);
            Assert.Equal("\"updated words\"", updated.Arguments);

            using var external = Process.Start(new ProcessStartInfo(other, "phase3") { WorkingDirectory = alternate.FullName })!;
            try
            {
                Assert.Equal(CommandStatus.Accepted, (await monitor.ExecuteAsync(program, ProgramAction.Start, null, 0, Token, updated)).Status);
                var started = await Until(async () => Assert.Single((await monitor.SampleAsync(true, Token)).Programs),
                    item => item.Worker?.Window is { State: WindowState.Minimized, Responding: true } && item.PrivateWorkingSetMiB > 0);
                var target = started.Worker!;
                Assert.False(File.Exists(Path.Combine(working.FullName, "fixture-evidence.txt")));
                Assert.Equal("updated words", await File.ReadAllTextAsync(Path.Combine(updatedWorking.FullName, "fixture-evidence.txt"), Token));
                Assert.Equal(files.ResolveImagePath(worker), target.Identity.ImagePath, ignoreCase: true);
                Assert.NotEqual(external.Id, target.Identity.Pid);
                Assert.NotNull(started.Uptime);
                await Task.Delay(350, Token);
                Assert.NotNull((await monitor.SampleAsync(true, Token)).Cpu);
                var old = new ProcessIdentity(target.Identity.ImagePath, target.Identity.Pid, target.Identity.CreatedAtUtc.AddTicks(-1));
                Assert.Equal(CommandStatus.IdentityChanged, platform.Control(program.Definition, ProgramAction.Force, old, null).Status);

                var activate = await monitor.ExecuteAsync(program, ProgramAction.Activate, target.Identity, target.Window!.Handle, Token);
                Assert.Contains(activate.Status, new[] { CommandStatus.Completed, CommandStatus.Denied });
                await Until(() => Task.FromResult(platform.Scan(allowed, false, Token)),
                    scan => scan.Workers.Any(item => item.Identity.Equals(target.Identity) && item.Window?.State == WindowState.Visible));
                var restart = await monitor.ExecuteAsync(program, ProgramAction.Restart, target.Identity, target.Window.Handle, Token);
                Assert.Equal(CommandStatus.Accepted, restart.Status);
                var restarted = await Until(async () => Assert.Single((await monitor.SampleAsync(true, Token)).Programs),
                    item => item.Worker is { Window: not null } value && !value.Identity.Equals(target.Identity));
                Assert.False(external.HasExited);
                var replacement = restarted.Worker!;
                Assert.Equal(CommandStatus.Completed,
                    (await monitor.ExecuteAsync(program, ProgramAction.Force, replacement.Identity, 0, Token)).Status);
                Assert.False(external.HasExited);
                Assert.Equal(ProcessState.Stopped, (await monitor.SampleAsync(true, Token)).Programs[0].Process);

                var workerFile = files.ResolveImagePath(worker);
                using (var first = Process.Start(new ProcessStartInfo(workerFile, "phase3") { WorkingDirectory = working.FullName })!)
                using (var second = Process.Start(new ProcessStartInfo(workerFile, "phase3") { WorkingDirectory = working.FullName })!)
                {
                    var multiple = await Until(async () => (await monitor.SampleAsync(true, Token)).Programs[0],
                        item => item.Process == ProcessState.Unknown);
                    Assert.Empty(multiple.Actions);
                    first.Kill();
                    second.Kill();
                    await Task.WhenAll(first.WaitForExitAsync(Token), second.WaitForExitAsync(Token));
                }

                using var abnormal = Process.Start(new ProcessStartInfo(files.ResolveImagePath(worker), "phase3-exit")
                    { WorkingDirectory = working.FullName })!;
                await Until(async () =>
                {
                    var current = Assert.Single((await monitor.SampleAsync(true, Token)).Programs);
                    abnormal.Refresh();
                    Assert.False(abnormal.HasExited, $"Fixture exited before observation. Exit code {ExitCode(abnormal)}. {current.Issue}");
                    return current;
                }, item => item.Worker is not null);
                await abnormal.WaitForExitAsync(Token);
                Assert.Equal(23, abnormal.ExitCode);
                Assert.Equal(ProcessState.ExitedUnexpectedly, (await monitor.SampleAsync(true, Token)).Programs[0].Process);

                using var hung = Process.Start(new ProcessStartInfo(files.ResolveImagePath(worker), "phase3-hung")
                    { WorkingDirectory = working.FullName })!;
                var unresponsive = await Until(async () => Assert.Single((await monitor.SampleAsync(false, Token)).Programs),
                    item => item.Worker?.Window is { Responding: false });
                Assert.DoesNotContain(ProgramAction.Close, unresponsive.Actions);
                Assert.Equal(CommandStatus.Denied, (await monitor.ExecuteAsync(program, ProgramAction.Close,
                    unresponsive.Worker!.Identity, unresponsive.Worker.Window!.Handle, Token)).Status);
                Assert.Equal(CommandStatus.Completed, (await monitor.ExecuteAsync(program, ProgramAction.Force,
                    unresponsive.Worker.Identity, 0, Token)).Status);
                Assert.False(external.HasExited);

                using var stubborn = Process.Start(new ProcessStartInfo(workerFile, "phase3-ignore-close") { WorkingDirectory = working.FullName })!;
                var refusing = await Until(async () => (await monitor.SampleAsync(false, Token)).Programs[0],
                    item => item.Worker?.Window is { Responding: true });
                var refusal = await monitor.ExecuteAsync(program, ProgramAction.Restart,
                    refusing.Worker!.Identity, refusing.Worker.Window!.Handle, Token);
                Assert.Equal(CommandStatus.TimedOut, refusal.Status);
                Assert.False(stubborn.HasExited);
                Assert.Equal(CommandStatus.Completed, (await monitor.ExecuteAsync(program, ProgramAction.Force,
                    refusing.Worker.Identity, 0, Token)).Status);
                output.WriteLine("Verified shortcut, batch arguments, separate install, PID reuse, multiple instances, window restore, restart, force, exit code, hung window and close timeout.");
            }
            finally
            {
                if (!external.HasExited) external.Kill();
                await external.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            foreach (var item in platform.Scan(allowed, false, CancellationToken.None).Workers)
            {
                if (!allowed.Contains(item.Identity.ImagePath)) continue;
                platform.Control(new(Guid.NewGuid(), "Owned fixture", item.Identity.ImagePath, allowed),
                    ProgramAction.Force, item.Identity, null);
            }
            await Until(() => Task.FromResult(platform.Scan(allowed, false, CancellationToken.None)), scan => scan.Workers.Count == 0);
            root.Delete(recursive: true);
        }
    }

    [Fact(SkipUnless = nameof(InteractiveFixture), Skip = "Set BAM_PHASE3_FIXTURE=1 in an interactive Windows session.")]
    public async Task FixtureClosesOnlyTheSelectedEmulatorAfterTheScriptExits()
    {
        var root = Directory.CreateTempSubdirectory("Bam paired close ");
        var files = new WindowsProgramFiles();
        var suffix = Guid.NewGuid().ToString("N");
        var executable = Path.Combine(root.FullName, $"BamOwnedScript{suffix}.exe");
        var emulator = Path.Combine(root.FullName, $"BamOwnedEmulator{suffix}.exe");
        List<Process> owned = [];
        try
        {
            await Compile(executable);
            File.Copy(executable, emulator);
            var catalog = new InstallationCatalog(files);
            var installation = catalog.Import(executable) with { EmulatorMonitor = new(files.ResolveImagePath(emulator), 4) };
            var store = new LocalProgramStore(root.FullName);
            await store.SaveAsync([installation], [], Token);
            using var platform = new WindowsProgramPlatform();
            using var monitor = new ProgramMonitor(catalog, platform, store, new LocalProgramEventStore(root.FullName), TimeProvider.System);
            await monitor.InitializeAsync(Token);
            var script = Start(executable, "phase3");
            var device = Start(emulator, "phase3 --index=4");
            var other = Start(emulator, "phase3 --index=7");
            var current = await Until(async () => Assert.Single((await monitor.SampleAsync(false, Token)).Programs),
                item => item.Actions.Contains(ProgramAction.CloseWithEmulator));
            var target = await Until(() => monitor.PrepareEmulatorCloseAsync(current.Installation, Token), candidate => candidate.Target is not null);
            Assert.Equal(device.Id, target.Target!.Identity.Pid);
            var result = await monitor.ExecuteAsync(current.Installation, ProgramAction.CloseWithEmulator,
                current.Worker!.Identity, current.Worker.Window!.Handle, Token, confirmedEmulator: target.Target);
            Assert.True(result.Status == CommandStatus.Completed, result.Reason);
            Assert.True(script.HasExited);
            Assert.True(device.HasExited);
            Assert.False(other.HasExited);
        }
        finally
        {
            foreach (var process in owned)
            {
                using (process)
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            await Until(() => Task.FromResult(RemoveFixture()), removed => removed);
        }

        bool RemoveFixture()
        {
            try { root.Delete(recursive: true); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return !root.Exists; }
        }

        Process Start(string path, string arguments)
        {
            var directory = root.CreateSubdirectory($"instance-{owned.Count}");
            var process = Process.Start(new ProcessStartInfo(path, arguments) { WorkingDirectory = directory.FullName })!;
            owned.Add(process);
            return process;
        }
    }

    private static int? ExitCode(Process process) => process.HasExited ? process.ExitCode : null;

    private static async Task Compile(string output)
    {
        var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
        var info = new ProcessStartInfo(framework)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/nologo", "/target:winexe", "/reference:System.Windows.Forms.dll",
            "/out:" + output, Path.Combine(AppContext.BaseDirectory, "ProcessFixture.cs") })
            info.ArgumentList.Add(argument);
        using var compiler = Process.Start(info)!;
        await compiler.WaitForExitAsync(Token);
        Assert.True(compiler.ExitCode == 0, await compiler.StandardOutput.ReadToEndAsync(Token));
    }

    private static void WriteLink(string link, string target, string working, string arguments, string iconPath)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(link);
            dynamic writer = shortcut!;
            writer.TargetPath = target;
            writer.WorkingDirectory = working;
            writer.Arguments = arguments;
            writer.IconLocation = $"{iconPath},1";
            writer.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static async Task<T> Until<T>(Func<Task<T>> read, Func<T, bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var result = await read();
            if (predicate(result)) return result;
            await Task.Delay(100, Token);
        } while (timer.Elapsed < TimeSpan.FromSeconds(12));
        throw new TimeoutException("未观察到预期的自有夹具状态。");
    }
}
