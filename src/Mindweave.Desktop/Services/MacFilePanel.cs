using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Mindweave.Desktop.Services;

/// <summary>
/// AppKit's save and open panels, run app-modally (<c>runModal</c>), which Avalonia's picker does not offer. See
/// <see cref="FilePanels"/> for why. Called on the UI thread, which is AppKit's main thread.
/// </summary>
internal static partial class MacFilePanel
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string UniformTypes = "/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers";

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary><c>NSModalResponseOK</c>.</summary>
    private const nint Ok = 1;

    /// <summary>Whether <paramref name="owner"/> is in a window of AppKit's (not in a headless test); the window itself.</summary>
    public static bool IsFor(TopLevel owner, out nint window)
    {
        var handle = OperatingSystem.IsMacOS() ? owner.TryGetPlatformHandle() : null;
        window = handle?.HandleDescriptor == "NSWindow" ? handle.Handle : 0;
        return window != 0;
    }

    /// <summary>The path picked for saving; null when cancelled.</summary>
    public static Task<string?> SaveAsync(nint window, string title, string suggestedFileName, IReadOnlyList<string> extensions) =>
        FromRunLoopAsync(() =>
        {
            var panel = Get(objc_getClass("NSSavePanel"), "savePanel");
            Set(panel, "setNameFieldStringValue:", Text(suggestedFileName));
            Set(panel, "setCanCreateDirectories:", true);
            return Run(panel, window, title, extensions) ? PathOf(Get(panel, "URL")) : null;
        });

    /// <summary>The paths of the files picked; none when cancelled.</summary>
    public static Task<IReadOnlyList<string>> OpenAsync(nint window, string title, IReadOnlyList<string> extensions) =>
        FromRunLoopAsync<IReadOnlyList<string>>(() =>
        {
            var panel = Get(objc_getClass("NSOpenPanel"), "openPanel");
            Set(panel, "setAllowsMultipleSelection:", true);
            if (!Run(panel, window, title, extensions))
            {
                return [];
            }

            var urls = Get(panel, "URLs");
            return [.. Enumerable.Range(0, (int)Get(urls, "count")).Select(i => PathOf(Get(urls, "objectAtIndex:", i))).OfType<string>()];
        });

    private static bool Run(nint panel, nint window, string title, IReadOnlyList<string> extensions)
    {
        Set(panel, "setTitle:", Text(title));

        // The window may have a theme of its own; a sheet took it from there.
        Set(panel, "setAppearance:", Get(window, "effectiveAppearance"));
        if (extensions.Count > 0)
        {
            Set(panel, "setAllowedContentTypes:", Types(extensions));
        }

        return Get(panel, "runModal") == Ok;
    }

    private static nint Types(IReadOnlyList<string> extensions)
    {
        NativeLibrary.Load(UniformTypes);
        var types = Get(objc_getClass("NSMutableArray"), "array");
        foreach (var extension in extensions)
        {
            var type = Get(objc_getClass("UTType"), "typeWithFilenameExtension:", Text(extension));
            if (type != 0)
            {
                Set(types, "addObject:", type);
            }
        }

        return types;
    }

    private static string? PathOf(nint url) => url == 0 ? null : Marshal.PtrToStringUTF8(Get(Get(url, "path"), "UTF8String"));

    private static nint Text(string text) => Get(objc_getClass("NSString"), "stringWithUTF8String:", text);

    /// <summary>
    /// Runs <paramref name="panel"/> from a one-shot timer of the main run loop. A modal panel runs the loop inside itself,
    /// and macOS never enters a run loop observer or the main dispatch queue a second time: started from an Avalonia
    /// job (which runs in one), a panel would hold back every background-priority job and most drawing until it closed.
    /// </summary>
    private static unsafe Task<T> FromRunLoopAsync<T>(Func<T> panel)
    {
        // Continues through the dispatcher, after the timer's call has returned.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action run = () =>
        {
            var pool = objc_autoreleasePoolPush();
            try
            {
                done.SetResult(panel());
            }
#pragma warning disable CA1031 // Handed on to the caller: an exception must not cross the native frames between.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.SetException(ex);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        };

        // The timer's context: version, info, retain, release, copyDescription.
        var context = stackalloc nint[5];
        context[1] = GCHandle.ToIntPtr(GCHandle.Alloc(run));
        var timer = CFRunLoopTimerCreate(0, 0, 0, 0, 0, (nint)(delegate* unmanaged<nint, nint, void>)&OnTimer, (nint)context);
        CFRunLoopAddTimer(CFRunLoopGetMain(), timer, Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes")));
        CFRelease(timer);
        return done.Task;
    }

    /// <summary>Fires once: an interval of 0 makes the timer one-shot.</summary>
    [UnmanagedCallersOnly]
    private static void OnTimer(nint timer, nint info)
    {
        var handle = GCHandle.FromIntPtr(info);
        var run = (Action)handle.Target!;
        handle.Free();
        run();
    }

    private static nint Get(nint target, string selector) => Send(target, sel_registerName(selector));

    private static nint Get(nint target, string selector, nint argument) => Send(target, sel_registerName(selector), argument);

    private static nint Get(nint target, string selector, string argument) => SendText(target, sel_registerName(selector), argument);

    private static void Set(nint target, string selector, nint value) => Send(target, sel_registerName(selector), value);

    private static void Set(nint target, string selector, bool value) => SendBool(target, sel_registerName(selector), value);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint target, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint target, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendText(nint target, nint selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendBool(nint target, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRunLoopGetMain();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRunLoopTimerCreate(nint allocator, double fireDate, double interval, ulong flags, nint order, nint callout, nint context);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRunLoopAddTimer(nint loop, nint timer, nint mode);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
