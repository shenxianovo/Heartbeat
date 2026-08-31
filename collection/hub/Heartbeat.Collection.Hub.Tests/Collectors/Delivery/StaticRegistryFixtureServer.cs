using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// A loopback static file server that hosts a real Registry directory tree, so Registry tests
/// exercise the same read path the Runtime uses against a deployed static directory instead of a
/// stubbed handler. The OS assigns the port, so parallel runs never collide and no test sleeps.
///
/// <see cref="Redirects" /> lets a test make the server answer a path with a 302, including one that
/// points outside the Registry, which is how the redirect boundary is proven.
/// </summary>
internal sealed class StaticRegistryFixtureServer : IDisposable
{
    public const string RegistryPathPrefix = "/collector-registry/v1/";

    private readonly HttpListener _listener;
    private readonly string _rootDirectory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;

    private StaticRegistryFixtureServer(HttpListener listener, string rootDirectory, Uri baseUri)
    {
        _listener = listener;
        _rootDirectory = rootDirectory;
        BaseUri = baseUri;
        _loop = Task.Run(ServeAsync);
    }

    /// <summary>The Registry base URI, for example <c>http://127.0.0.1:53211/collector-registry/v1/</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>Absolute request path to <c>Location</c> value; the server answers those paths with 302.</summary>
    public ConcurrentDictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How many requests each absolute path received. Installation tests use it to prove that a
    /// repeated install does not download again and that a failure is not retried behind the caller.
    /// </summary>
    public ConcurrentDictionary<string, int> RequestCounts { get; } = new(StringComparer.Ordinal);

    public static StaticRegistryFixtureServer Start(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        Directory.CreateDirectory(rootDirectory);

        for (var attempt = 0; ; attempt++)
        {
            var port = FreeLoopbackPort();
            var prefix = $"http://127.0.0.1:{port}{RegistryPathPrefix}";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Close();
                continue;
            }
            return new StaticRegistryFixtureServer(
                listener,
                Path.GetFullPath(rootDirectory),
                new Uri(prefix, UriKind.Absolute));
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                Respond(context);
            }
            catch (HttpListenerException)
            {
                // The client went away mid-response; nothing for the fixture to do.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private void Respond(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        RequestCounts.AddOrUpdate(path, 1, (_, count) => count + 1);
        if (Redirects.TryGetValue(path, out var location))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Found;
            context.Response.Headers["Location"] = location;
            return;
        }

        if (!path.StartsWith(RegistryPathPrefix, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var relative = path[RegistryPathPrefix.Length..];
        var file = Path.GetFullPath(Path.Combine(_rootDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(_rootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var bytes = File.ReadAllBytes(file);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = file.EndsWith(".json", StringComparison.Ordinal)
            ? "application/json"
            : "application/octet-stream";
        context.Response.ContentLength64 = bytes.LongLength;
        context.Response.OutputStream.Write(bytes);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was closed under the accept loop; that is the shutdown path.
        }
        _stopping.Dispose();
    }
}
