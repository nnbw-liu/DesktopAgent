using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 最小的 OpenAI 兼容 SSE 服务器：按顺序回预置流，并记录收到的请求体。
/// 用它就能在没有网络 / 密钥的情况下验证「请求长什么样」。
/// </summary>
internal sealed class FakeOpenAiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly List<string> _bodies = [];
    private readonly List<string> _requests = [];
    private int _served;

    public FakeOpenAiServer()
    {
        var port = FindPort();
        Endpoint = new Uri($"http://127.0.0.1:{port}/v1");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public Uri Endpoint { get; }

    /// <summary>按请求顺序记录的请求体（含 system prompt / 工具 / 历史消息）。</summary>
    public IReadOnlyList<string> RequestBodies
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>追加一段 SSE 响应；请求数超过预置数量时重复最后一段。</summary>
    public void Respond(string body) => _bodies.Add(body.Replace("\r\n", "\n"));

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // 已经停了。
        }

        _loop.GetAwaiter().GetResult();
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            string request;

            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                request = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            lock (_requests)
            {
                _requests.Add(request);
            }

            var body = _bodies.Count == 0
                ? "data: [DONE]\n\n"
                : _bodies[Math.Min(_served, _bodies.Count - 1)];
            _served++;

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.SendChunked = true;

            var payload = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
            context.Response.Close();
        }
    }

    private static int FindPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
