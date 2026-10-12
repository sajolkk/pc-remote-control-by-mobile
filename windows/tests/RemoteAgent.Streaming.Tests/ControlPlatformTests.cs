using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Files;
using RemoteAgent.Core.Input;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Display;
using RemoteAgent.Windows.Files;
using RemoteAgent.Windows.Input;
using RemoteAgent.Windows.Shell;
using Xunit;
using Xunit.Abstractions;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// The Windows side of input, window and file management, exercised without changing anything.
/// </summary>
/// <remarks>
/// <para><b>Strictly read-only.</b> These tests run on a developer's live desktop. They ask Windows
/// questions — how big is the virtual screen, where is the pointer, which desktop has input, which
/// windows are open, where are the user's folders — and they check native structure layouts. They
/// never call <c>Send</c> on the injector, never act on a real window id, and never open, recycle
/// or modify a file. The one <c>Act</c> call uses ids that cannot name a window.</para>
///
/// <para>They exist because the platform layer is the part unit tests cannot reach: a wrong
/// structure size or a mistyped entry point compiles perfectly and fails only on a real PC. Each
/// returns early, passing, where there is no interactive desktop to ask.</para>
/// </remarks>
[Trait("Category", "Hardware")]
public sealed class ControlPlatformTests(ITestOutputHelper output)
{
    private static Type Native(string nested) =>
        typeof(SendInputInjector).Assembly.GetType("RemoteAgent.Windows.Interop.NativeMethods+" + nested, throwOnError: true)!;

    // --- Native layouts ---

    [Fact]
    public void TheInputStructureIsTheSizeWindowsExpects()
    {
        if (!Environment.Is64BitProcess)
        {
            return;
        }

        // SendInput is told sizeof(INPUT) and rejects every event if it is wrong. On 64-bit
        // Windows: a 4-byte type, 4 bytes of padding, and a 32-byte union.
        Assert.Equal(32, Marshal.SizeOf(Native("MouseInput")));
        Assert.Equal(24, Marshal.SizeOf(Native("KeyboardInput")));
        Assert.Equal(32, Marshal.SizeOf(Native("InputUnion")));
        Assert.Equal(40, Marshal.SizeOf(Native("Input")));

        // The union must start after the padding, not straight after the type.
        Assert.Equal(8, (int)Marshal.OffsetOf(Native("Input"), "Data"));
    }

    [Fact]
    public void TheShellFileOperationStructureIsTheSizeWindowsExpects()
    {
        if (!Environment.Is64BitProcess)
        {
            return;
        }

        Type operation = Native("ShFileOpStruct");

        Assert.Equal(56, Marshal.SizeOf(operation));
        Assert.Equal(16, (int)Marshal.OffsetOf(operation, "From"));
        Assert.Equal(32, (int)Marshal.OffsetOf(operation, "Flags"));
        Assert.Equal(36, (int)Marshal.OffsetOf(operation, "AnyOperationsAborted"));
        Assert.Equal(48, (int)Marshal.OffsetOf(operation, "ProgressTitle"));
    }

    // --- Input: queries only ---

    [Fact]
    public void TheVirtualScreenCoversEveryMonitor()
    {
        var injector = new SendInputInjector(NullLogger<SendInputInjector>.Instance);
        PixelRect screen = injector.GetVirtualScreen();

        output.WriteLine($"Virtual screen: {screen}");
        if (screen.Width <= 0 || screen.Height <= 0)
        {
            return;
        }

        IReadOnlyList<MonitorInfo> monitors = new DxgiMonitorProvider(NullLogger<DxgiMonitorProvider>.Instance).GetMonitors();
        foreach (MonitorInfo monitor in monitors)
        {
            output.WriteLine($"Monitor {monitor.Name}: {monitor.X},{monitor.Y} {monitor.Width}x{monitor.Height} scale {monitor.Scale}");
        }

        // The test host is not DPI-aware the way the session agent is, so Windows may report the
        // virtual screen to this process in scaled units. The comparison below only holds when it
        // did not, i.e. on an unscaled desktop; the mapping arithmetic itself is unit tested.
        if (monitors.Count == 0 || monitors.Any(m => Math.Abs(m.Scale - 1.0) > 0.001))
        {
            return;
        }

        foreach (MonitorInfo monitor in monitors)
        {
            Assert.True(monitor.X >= screen.X && monitor.Y >= screen.Y);
            Assert.True(monitor.X + monitor.Width <= screen.X + screen.Width);
            Assert.True(monitor.Y + monitor.Height <= screen.Y + screen.Height);

            // Every corner of every monitor maps into the absolute range and back onto itself.
            foreach ((double x, double y) in new[] { (0.0, 0.0), (1.0, 1.0), (0.5, 0.5) })
            {
                var rect = new PixelRect(monitor.X, monitor.Y, monitor.Width, monitor.Height);
                (int px, int py) = InputCoordinates.MonitorPointToPixel(x, y, rect);
                (int ax, int ay) = InputCoordinates.PixelToAbsolute(px, py, screen);

                Assert.InRange(ax, 0, InputCoordinates.AbsoluteMax);
                Assert.InRange(ay, 0, InputCoordinates.AbsoluteMax);
                Assert.Equal(px, InputCoordinates.AbsoluteToPixel(ax, screen.X, screen.Width));
                Assert.Equal(py, InputCoordinates.AbsoluteToPixel(ay, screen.Y, screen.Height));
            }
        }
    }

    [Fact]
    public void ThePointerPositionAndTheInputDesktopCanBeRead()
    {
        var injector = new SendInputInjector(NullLogger<SendInputInjector>.Instance);

        // Neither call changes anything; both must simply not throw, which is what a wrong
        // signature would do.
        (int X, int Y)? cursor = injector.GetCursorPosition();
        InputDesktopState state = injector.GetDesktopState();

        output.WriteLine($"Cursor: {cursor?.ToString() ?? "(unreadable)"}; input desktop: {state}");

        Assert.True(Enum.IsDefined(state));
    }

    [Fact]
    public void AnEmptyBatchIsAcceptedWithoutCallingWindows()
    {
        // The only Send in this file, and it sends nothing.
        Assert.True(new SendInputInjector(NullLogger<SendInputInjector>.Instance).Send([]));
    }

    // --- Windows: listing only ---

    [Fact]
    public void OpenWindowsCanBeListedWithWellFormedIds()
    {
        var manager = new WindowsWindowManager(
            new WindowInspector(NullLogger<WindowInspector>.Instance),
            NullLogger<WindowsWindowManager>.Instance);

        IReadOnlyList<WindowInfo> windows = manager.List();
        output.WriteLine($"{windows.Count} window(s) listed.");

        Assert.Equal(windows.Count, windows.Select(w => w.Id).Distinct().Count());
        Assert.True(windows.Count(w => w.IsForeground) <= 1);

        Assert.All(windows, window => Assert.Matches("^w[0-9a-f]+-[0-9a-f]+$", window.Id));

        // The shell's own surfaces are not application windows.
        Assert.DoesNotContain(windows, w => w.Title == "Program Manager" && w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase));

        if (windows.Count > 0 && windows.Any(w => w.IsForeground))
        {
            Assert.True(windows[0].IsForeground, "the foreground window is listed first");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("w")]
    [InlineData("w0-0")]
    [InlineData("w1-")]
    [InlineData("12345")]
    [InlineData("wzz-zz")]
    [InlineData("w7fffffffffffffff-ffffffff")]
    [InlineData("w-1-1")]
    [InlineData("../w1-1")]
    public void AnIdThatNamesNoWindowIsReportedAsGoneAndNothingIsTouched(string id)
    {
        var manager = new WindowsWindowManager(
            new WindowInspector(NullLogger<WindowInspector>.Instance),
            NullLogger<WindowsWindowManager>.Instance);

        // "Restore" is the least intrusive action there is, and it is never reached: every one of
        // these ids fails validation first.
        Assert.Null(manager.Act(id, WindowAction.Restore));
    }

    [Fact]
    public void AWindowIdIsBoundToTheProcessThatOwnedIt()
    {
        var manager = new WindowsWindowManager(
            new WindowInspector(NullLogger<WindowInspector>.Instance),
            NullLogger<WindowsWindowManager>.Instance);

        WindowInfo? real = manager.List().FirstOrDefault();
        if (real is null)
        {
            return;
        }

        // The same handle with a different process id is what a recycled handle looks like. It
        // must be refused — and because it is refused, this real window is not acted on.
        string handle = real.Id[..real.Id.IndexOf('-', StringComparison.Ordinal)];
        Assert.Null(manager.Act(handle + "-fffffffe", WindowAction.Restore));
    }

    // --- Files: discovery only ---

    [Fact]
    public void TheBuiltInRootsAreRealFoldersAndFixedDrives()
    {
        IReadOnlyList<FileRoot> roots = new WindowsDefaultFileRoots(NullLogger<WindowsDefaultFileRoots>.Instance).GetDefaultRoots();

        foreach (FileRoot root in roots)
        {
            output.WriteLine($"{root.Label} -> {root.Path}");
        }

        Assert.NotEmpty(roots);
        Assert.All(roots, root =>
        {
            Assert.True(root.Writable);
            Assert.False(string.IsNullOrWhiteSpace(root.Label));
            Assert.True(Directory.Exists(root.Path), $"{root.Path} does not exist.");

            // Every built-in root is a path the policy itself accepts.
            Assert.True(FilePathPolicy.TryCanonicalize(root.Path, out _, out PathRejection rejection), $"{root.Path}: {rejection}");
        });

        // The system drive is always fixed, always ready, and labelled the way Explorer labels it.
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        FileRoot drive = Assert.Single(roots, root => string.Equals(root.Path, systemDrive, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith($"({systemDrive.TrimEnd('\\')})", drive.Label, StringComparison.Ordinal);

        // A signed-in person has the personal folders; a service account running the tests on a
        // build machine may not.
        if (Environment.UserInteractive && Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))
        {
            Assert.Contains(roots, root => root.Label == "Downloads");
            Assert.Contains(roots, root => root.Label == "Documents");
            Assert.Contains(roots, root => root.Label == "Desktop");
        }
    }

    [Fact]
    public void TheBuiltInRootsBecomeTheRootsViewAPhoneSees()
    {
        // The real defaults behind the real manager, asked only to list its roots.
        var roots = new ConfiguredFileRoots(
            () => new RemoteAgent.Core.Configuration.FileOptions(),
            new WindowsDefaultFileRoots(NullLogger<WindowsDefaultFileRoots>.Instance));

        var files = new FileManager(roots, new RefusingShell(), NullLogger<FileManager>.Instance);

        FileResult<FileListResult> view = files.List(null);

        Assert.True(view.Succeeded, view.Detail);
        Assert.Equal(string.Empty, view.Value!.Path);
        Assert.NotEmpty(view.Value.Entries);
        Assert.All(view.Value.Entries, entry =>
        {
            Assert.True(entry.IsRoot);
            Assert.True(entry.IsDirectory);
            Assert.Null(entry.ReadOnly);
        });

        // And with the policy unknown, nothing at all is offered.
        var unknown = new FileManager(
            new ConfiguredFileRoots(() => null, new WindowsDefaultFileRoots(NullLogger<WindowsDefaultFileRoots>.Instance)),
            new RefusingShell(),
            NullLogger<FileManager>.Instance);

        Assert.Empty(unknown.List(null).Value!.Entries);
        Assert.Equal(FileStatus.AccessDenied, unknown.List(Environment.SystemDirectory).Status);
    }

    /// <summary>A shell that does nothing, so no code path here can open or recycle anything.</summary>
    private sealed class RefusingShell : IFileShell
    {
        public RecycleOutcome MoveToRecycleBin(string path, out string? detail)
        {
            detail = "Not in a test.";
            return RecycleOutcome.Refused;
        }

        public bool Open(string path, out string? detail)
        {
            detail = "Not in a test.";
            return false;
        }
    }
}
