using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace AxLeakRepro;

// Reproduces AvaloniaUI/Avalonia#8240 on macOS without an external accessibility client:
// the NSAccessibility getters are invoked in-process on the main thread, which drives the
// same automation.mm code paths AppKit drives on behalf of VoiceOver or any AX client.
//
// Exit code: 0 when every detached view was collected, 1 when any survived, 2 on setup failure.

sealed class Payload
{
    public byte[] Bytes { get; } = new byte[10 * 1024 * 1024];
}

static class Program
{
    public static string? NativePath;
    public static int Iterations = 5;

    [STAThread]
    static int Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--native") NativePath = args[++i];
            else if (args[i] == "--iterations") Iterations = int.Parse(args[++i]);
        }

        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (NativePath != null)
        {
            Console.WriteLine($"native={NativePath}");
            builder = builder.With(new AvaloniaNativePlatformOptions { AvaloniaNativeLibraryPath = NativePath });
        }
        else
        {
            Console.WriteLine("native=<package default>");
        }

        return builder.StartWithClassicDesktopLifetime(args);
    }
}

sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                int code;
                try
                {
                    code = await Scenario.RunAsync(window);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"FAILED: {e}");
                    code = 2;
                }
                desktop.Shutdown(code);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

sealed class MainWindow : Window
{
    public ContentControl Host { get; } = new();

    public MainWindow()
    {
        Title = "AX leak repro";
        Width = 640;
        Height = 420;
        Content = Host;
    }
}

static class Scenario
{
    static StackPanel BuildView(int i)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, DataContext = new Payload() };
        panel.Children.Add(new TextBlock { Text = $"View {i}" });
        panel.Children.Add(new Button { Content = $"Button {i}" });
        panel.Children.Add(new TextBox { Text = $"Text {i}" });
        panel.Children.Add(new CheckBox { Content = "Check", IsChecked = i % 2 == 0 });
        panel.Children.Add(new Slider { Minimum = 0, Maximum = 10, Value = i });
        panel.Children.Add(new ScrollViewer { Height = 60, Content = new TextBlock { Text = string.Join('\n', Enumerable.Range(0, 20)) } });
        return panel;
    }

    static async Task Settle(MainWindow window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(30);
        }
    }

    static void FullGc()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    static int CountAlive(List<WeakReference<Control>> refs) => refs.Count(r => r.TryGetTarget(out _));

    public static async Task<int> RunAsync(MainWindow window)
    {
        await Settle(window);
        var detached = new List<WeakReference<Control>>();

        // Phase 1: show a view, let an AX client walk it, detach it, collect.
        for (var i = 0; i < Program.Iterations; i++)
        {
            var view = BuildView(i);
            window.Host.Content = view;
            await Settle(window);

            var visited = AxWalker.Walk(window);
            await Settle(window);

            window.Host.Content = null;
            detached.Add(new WeakReference<Control>(view));
            view = null!;
            await Settle(window);

            FullGc();
            Console.WriteLine($"phase1 iter={i} ax_nodes={visited} detached={detached.Count} alive={CountAlive(detached)} heap_mb={GC.GetTotalMemory(false) / 1048576}");
        }

        var phase1Alive = CountAlive(detached);

        // Phase 2: a live control loses its native element while its peer stays alive
        // (hidden child dropped from the parent's child list), then raises ChildrenChanged
        // with no element behind it, then comes back and must get a fresh element.
        var live = BuildView(100);
        var inner = new StackPanel();
        inner.Children.Add(new Button { Content = "inner" });
        live.Children.Add(inner);
        window.Host.Content = live;
        await Settle(window);
        var before = AxWalker.Walk(window);
        await Settle(window);

        inner.IsVisible = false;
        await Settle(window);
        FullGc();
        inner.Children.Add(new Button { Content = "added while hidden" });
        await Settle(window);
        inner.IsVisible = true;
        await Settle(window);
        var after = AxWalker.Walk(window);
        await Settle(window);
        Console.WriteLine($"phase2 ax_nodes_before={before} ax_nodes_after={after} (expected after > before)");

        window.Host.Content = null;
        detached.Add(new WeakReference<Control>(live));
        live = null!;
        inner = null!;
        await Settle(window);
        FullGc();
        var finalAlive = CountAlive(detached);
        Console.WriteLine($"RESULT phase1_alive={phase1Alive}/{Program.Iterations} final_alive={finalAlive}/{detached.Count} heap_mb={GC.GetTotalMemory(true) / 1048576}");
        return finalAlive == 0 ? 0 : 1;
    }
}

static class AxWalker
{
    const int MaxDepth = 40;
    const int MaxNodes = 20000;

    static readonly IntPtr SelSharedApplication = ObjC.Sel("sharedApplication");
    static readonly IntPtr SelWindows = ObjC.Sel("windows");
    static readonly IntPtr SelCount = ObjC.Sel("count");
    static readonly IntPtr SelObjectAtIndex = ObjC.Sel("objectAtIndex:");
    static readonly IntPtr SelChildren = ObjC.Sel("accessibilityChildren");
    static readonly IntPtr SelRole = ObjC.Sel("accessibilityRole");
    static readonly IntPtr SelSubrole = ObjC.Sel("accessibilitySubrole");
    static readonly IntPtr SelTitle = ObjC.Sel("accessibilityTitle");
    static readonly IntPtr SelIdentifier = ObjC.Sel("accessibilityIdentifier");
    static readonly IntPtr SelValue = ObjC.Sel("accessibilityValue");
    static readonly IntPtr SelHelp = ObjC.Sel("accessibilityHelp");
    static readonly IntPtr SelParent = ObjC.Sel("accessibilityParent");
    static readonly IntPtr SelTopLevel = ObjC.Sel("accessibilityTopLevelUIElement");
    static readonly IntPtr SelWindow = ObjC.Sel("accessibilityWindow");
    static readonly IntPtr SelFrame = ObjC.Sel("accessibilityFrame");
    static readonly IntPtr SelIsElement = ObjC.Sel("isAccessibilityElement");
    static readonly IntPtr SelHScroll = ObjC.Sel("accessibilityHorizontalScrollBar");
    static readonly IntPtr SelVScroll = ObjC.Sel("accessibilityVerticalScrollBar");
    static readonly IntPtr SelFocused = ObjC.Sel("accessibilityFocusedUIElement");
    static readonly IntPtr SelHitTest = ObjC.Sel("accessibilityHitTest:");

    static bool _verbose = true;

    public static int Walk(Window avaloniaWindow)
    {
        var handle = avaloniaWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("The window has no native handle.");

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            if (_verbose)
            {
                var app = ObjC.Send(ObjC.objc_getClass("NSApplication"), SelSharedApplication);
                var windows = ObjC.Send(app, SelWindows);
                var count = (int)ObjC.SendNUInt(windows, SelCount);
                Console.WriteLine($"nswindow={ObjC.ClassName(handle)} app_windows={count}");
                for (var i = 0; i < count; i++)
                    Console.WriteLine($"  window[{i}]={ObjC.ClassName(ObjC.Send(windows, SelObjectAtIndex, (nuint)i))}");
            }

            _ = ObjC.Send(handle, SelFocused);
            _ = ObjC.Send(handle, SelIdentifier);
            var visited = 0;
            Walk(handle, 0, ref visited);
            _verbose = false;
            return visited;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    static void Walk(IntPtr element, int depth, ref int visited)
    {
        if (depth > MaxDepth || visited > MaxNodes)
            return;
        visited++;

        var className = ObjC.ClassName(element);
        if (_verbose && depth <= 3)
            Console.WriteLine($"  {new string(' ', depth * 2)}{className}");
        if (className == "AvnAccessibilityElement")
        {
            _ = ObjC.Send(element, SelRole);
            _ = ObjC.Send(element, SelSubrole);
            _ = ObjC.Send(element, SelTitle);
            _ = ObjC.Send(element, SelIdentifier);
            _ = ObjC.Send(element, SelValue);
            _ = ObjC.Send(element, SelHelp);
            _ = ObjC.SendBool(element, SelIsElement);
            _ = ObjC.Send(element, SelParent);
            _ = ObjC.Send(element, SelTopLevel);
            _ = ObjC.Send(element, SelWindow);
            _ = ObjC.SendRect(element, SelFrame);
            _ = ObjC.Send(element, SelHScroll);
            _ = ObjC.Send(element, SelVScroll);
        }
        else if (className == "AvnView")
        {
            var frame = ObjC.SendRect(element, SelFrame);
            var point = new ObjC.CGPoint { X = frame.X + frame.W / 2, Y = frame.Y + frame.H / 2 };
            _ = ObjC.SendPoint(element, SelHitTest, point);
        }
        else if (!className.StartsWith("Avn", StringComparison.Ordinal))
        {
            return;
        }

        var children = ObjC.Send(element, SelChildren);
        if (children == IntPtr.Zero)
            return;
        var count = (int)ObjC.SendNUInt(children, SelCount);
        for (var i = 0; i < count; i++)
            Walk(ObjC.Send(children, SelObjectAtIndex, (nuint)i), depth + 1, ref visited);
    }
}

static class ObjC
{
    const string Lib = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect { public double X, Y, W, H; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint { public double X, Y; }

    [DllImport(Lib)] public static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] public static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] public static extern IntPtr object_getClassName(IntPtr obj);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, nuint arg);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendPoint(IntPtr self, IntPtr sel, CGPoint point);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nuint SendNUInt(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendBool(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGRect SendRect(IntPtr self, IntPtr sel);

    public static IntPtr Sel(string name) => sel_registerName(name);

    // KVO observation swizzles an object's class to NSKVONotifying_<Class>; report the real one.
    public static string ClassName(IntPtr obj)
    {
        var name = obj == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(object_getClassName(obj)) ?? "";
        const string kvo = "NSKVONotifying_";
        return name.StartsWith(kvo, StringComparison.Ordinal) ? name.Substring(kvo.Length) : name;
    }
}
