using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Mindweave.Desktop.Markdown;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Health check 2026-11-12, item 6: model text must not make the application fetch anything. A Markdown image in an
/// answer or summary would otherwise be loaded at once, while streaming and on every reopening, so an answer could send
/// conversation text to a server in the image address. Images are outside the first version (architecture §3), so no
/// image source is loaded at all: not over the network, not from a local file.
/// </summary>
public sealed class MarkdownImageTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private int _connections;

    public MarkdownImageTests()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public void Dispose() => _listener.Stop();

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_image_in_model_text_is_never_fetched(bool streaming)
    {
        var markdown = $"前文。\n\n![图](http://127.0.0.1:{Port}/p.png?d=SECRET)\n\n后文 ![内联](http://127.0.0.1:{Port}/q.png)。";

        var (window, renderer) = await RenderAsync(markdown, streaming);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, Volatile.Read(ref _connections));
        Assert.All(Images(renderer), image => Assert.Null(image.Source));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_local_file_image_is_not_loaded()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mindweave-image-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), TestContext.Current.CancellationToken);
        try
        {
            var (window, renderer) = await RenderAsync($"![本地]({new Uri(file).AbsoluteUri})", streaming: false);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.All(Images(renderer), image => Assert.Null(image.Source));
            window.Close();
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static IEnumerable<Image> Images(Visual renderer) => renderer.GetVisualDescendants().OfType<Image>();

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _connections);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task<(Window Window, MarkdownRenderer Renderer)> RenderAsync(string markdown, bool streaming)
    {
        MarkdownSetup.EnsureRegistered();
        var renderer = new MarkdownRenderer();
        var window = new Window { Content = renderer, Width = 600, Height = 400 };
        window.Show();
        if (!streaming)
        {
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdig.Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));
        }
        else
        {
            var live = new ObservableStringBuilder();
            renderer.MarkdownBuilder = live;
            for (var i = 0; i < markdown.Length; i += 9)
            {
                live.Append(markdown.Substring(i, Math.Min(9, markdown.Length - i)));
                await UiWait.UntilAsync(() => renderer.DocumentUpdate?.Version == live.Version);
            }
        }

        window.UpdateLayout();
        return (window, renderer);
    }
}
