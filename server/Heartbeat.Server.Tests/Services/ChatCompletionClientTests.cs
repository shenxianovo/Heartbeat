using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Heartbeat.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// LLM 传输层（ADR-042 §9 之后的形状）：分块的 delta 提取、思考控制参数、以及两件靠 fake
/// HttpMessageHandler 才钉得住的事——HttpClient.Timeout 不再掐流、非流式的时限自己带。
///
/// 这些用例全是踩过的坑：8/7 那次"生成超时"就是因为思考期的帧只有 reasoning_content，
/// 而 120s 的 HttpClient.Timeout 还在后面等着掐断一条本该跑 181s 的正常生成。
/// </summary>
public class ChatCompletionClientTests
{
    // ---- ChatCompletionClient.ExtractChunk（流式分块，ADR-042 §9）----

    [Fact]
    public void ExtractChunk_ContentOnly_ReturnsContent()
    {
        var data = """{"choices":[{"index":0,"delta":{"content":"那一天"},"finish_reason":null}]}""";

        var (reasoning, content) = ChatCompletionClient.ExtractChunk(data);

        Assert.Null(reasoning);
        Assert.Equal("那一天", content);
    }

    [Fact]
    public void ExtractChunk_ReasoningOnly_ReturnsReasoning_ContentStaysNull()
    {
        // 本次 bug 的形状：content 是空串、内容全在 reasoning_content 里，持续 175 秒。
        var data = """{"choices":[{"index":0,"delta":{"content":"","reasoning_content":"先看看这天"}}]}""";

        var (reasoning, content) = ChatCompletionClient.ExtractChunk(data);

        Assert.Equal("先看看这天", reasoning);
        Assert.Null(content);
    }

    [Fact]
    public void ExtractChunk_ReasoningAlias_AlsoRecognized()
    {
        // 部分兼容网关把字段叫 reasoning：认它，否则换个网关就退化成"只有静默"。
        var data = """{"choices":[{"index":0,"delta":{"reasoning":"想"}}]}""";

        Assert.Equal("想", ChatCompletionClient.ExtractChunk(data).Reasoning);
    }

    [Fact]
    public void ExtractChunk_BothPresent_ReturnsBoth()
    {
        var data = """{"choices":[{"index":0,"delta":{"reasoning_content":"过渡一下","content":"下午你"}}]}""";

        var (reasoning, content) = ChatCompletionClient.ExtractChunk(data);

        Assert.Equal("过渡一下", reasoning);
        Assert.Equal("下午你", content);
    }

    [Theory]
    // 首块通常只带 role，两边都没有——跳过，不是失败。
    [InlineData("""{"choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""")]
    // 空 content 归一成 null：拼进叙事没有意义，也不该被当成"有正文"。
    [InlineData("""{"choices":[{"index":0,"delta":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"index":0,"delta":{"content":"","reasoning_content":""}}]}""")]
    // 收尾块的 choices 可能是空数组（某些供应商在 usage 块里这么发）。
    [InlineData("""{"choices":[],"usage":{"total_tokens":42}}""")]
    // 缺 delta。
    [InlineData("""{"choices":[{"index":0,"finish_reason":"stop"}]}""")]
    [InlineData("""{"choices":[{"index":0,"delta":{}}]}""")]
    [InlineData("{}")]
    // 上游偶发的半截行：宽容跳过，不能让整条流失败。
    [InlineData("""{"choices":[{"delta":{"content":""")]
    [InlineData("not-json")]
    public void ExtractChunk_NothingUsable_ReturnsNullPair(string data)
    {
        var (reasoning, content) = ChatCompletionClient.ExtractChunk(data);

        Assert.Null(reasoning);
        Assert.Null(content);
    }

    // ---- 思考控制参数（Q3）：配置 → 请求体 ----

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData(" HIGH ")] // 大小写与空格都规范化，配置文件里手写不该踩坑
    public async Task Request_KnownEffort_CarriesReasoningEffort(string effort)
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(ContentBody)));
        var client = CreateClient(handler, effort);

        await client.CompleteAsync("system", "user");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(effort.Trim().ToLowerInvariant(), doc.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("default")]
    [InlineData("Default")]
    [InlineData("ultra-thinking")] // 未知值不传也不抛：配置写错不该让功能死
    public async Task Request_DefaultOrUnknownEffort_CarriesNoThinkingParameter(string effort)
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(ContentBody)));
        var client = CreateClient(handler, effort);

        await client.CompleteAsync("system", "user");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("disabled")]
    public async Task Request_NoneEffort_DisablesThinking(string effort)
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(ContentBody)));
        var client = CreateClient(handler, effort);

        await client.CompleteAsync("system", "user");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("disabled", doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task StreamingRequest_AlsoCarriesEffort()
    {
        // 流式与非流式共用一个 BuildRequest，但"两边都带"是配置项的语义要求，值得单独钉一枪。
        var handler = new CapturingHandler((_, ct) => Task.FromResult(SseResponse(
            ct,
            (TimeSpan.Zero, DataFrame("""{"choices":[{"delta":{"content":"文"}}]}""")),
            (TimeSpan.Zero, "data: [DONE]\n\n"))));
        var client = CreateClient(handler, "low");

        await DrainAsync(client.CompleteStreamAsync("system", "user"));

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("low", doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    // ---- 流式分块的产出顺序与失败语义 ----

    [Fact]
    public async Task CompleteStreamAsync_FrameWithBoth_YieldsReasoningBeforeContent()
    {
        var handler = new CapturingHandler((_, ct) => Task.FromResult(SseResponse(
            ct,
            (TimeSpan.Zero, DataFrame("""{"choices":[{"delta":{"reasoning_content":"想","content":"写"}}]}""")),
            (TimeSpan.Zero, "data: [DONE]\n\n"))));

        var chunks = await DrainAsync(CreateClient(handler).CompleteStreamAsync("s", "u"));

        // 前端的"正在思考"必须先于它解释的那段正文。
        Assert.Equal([LlmChunk.OfReasoning("想"), LlmChunk.OfContent("写")], chunks);
    }

    [Fact]
    public async Task CompleteStreamAsync_ReasoningWithoutAnyContent_StillFails()
    {
        // "只有思考没有正文"不是成功：不能让空叙事被当成成功落库。
        var handler = new CapturingHandler((_, ct) => Task.FromResult(SseResponse(
            ct,
            (TimeSpan.Zero, DataFrame("""{"choices":[{"delta":{"reasoning_content":"想了很久"}}]}""")),
            (TimeSpan.Zero, "data: [DONE]\n\n"))));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(CreateClient(handler).CompleteStreamAsync("s", "u")));

        Assert.Contains("无内容", ex.Message);
    }

    [Fact]
    public async Task CompleteStreamAsync_CallerCancels_BubblesOperationCanceled()
    {
        // 客户端断开/上层时限属于上层语义：不许伪装成 ChatCompletionException（否则编排层分不清
        // "断开"和"上游挂了"，会给一条没人听的流发 error）。
        var handler = new CapturingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse(ContentBody);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DrainAsync(CreateClient(handler).CompleteStreamAsync("s", "u", cts.Token)));
    }

    // ---- Q4：时限交给 CTS，HttpClient.Timeout 不再掐流 ----

    [Fact]
    public async Task CompleteStreamAsync_SlowBody_ReadsLastChunk_NoHiddenTimeLimit()
    {
        // 8/7 的真实形状：头 0.16s 就到，最后一块 181s 才到。头到得快不代表流短——传输层不许
        // 自带任何隐藏上限（Program.cs 的 Timeout = Infinite），时限一律由调用方的 CTS 说话。
        var handler = new CapturingHandler((_, ct) => Task.FromResult(SseResponse(
            ct,
            (TimeSpan.Zero, DataFrame("""{"choices":[{"delta":{"reasoning_content":"想"}}]}""")),
            (TimeSpan.FromMilliseconds(300), DataFrame("""{"choices":[{"delta":{"content":"前半"}}]}""")),
            (TimeSpan.FromMilliseconds(300), DataFrame("""{"choices":[{"delta":{"content":"后半"}}]}""")),
            (TimeSpan.Zero, "data: [DONE]\n\n"))));
        var client = CreateClient(handler, httpTimeout: Timeout.InfiniteTimeSpan);

        var started = Stopwatch.StartNew();
        var chunks = await DrainAsync(client.CompleteStreamAsync("s", "u"));

        Assert.Equal(
            [LlmChunk.OfReasoning("想"), LlmChunk.OfContent("前半"), LlmChunk.OfContent("后半")],
            chunks);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(600), "正文必须真的拖过一段时间，否则这条用例什么都没证明");
    }

    [Fact]
    public async Task CompleteStreamAsync_FiniteHttpTimeout_DoesNotEvenReachStreamedBody()
    {
        // 实测校正一个流行的误解（也是这次排障时的工作假设）：HttpClient.Timeout 覆盖的是"拿到响应
        // 头"，以及默认 ResponseContentRead 下的缓冲正文；ResponseHeadersRead 之后自己读的那段流
        // 不在它管辖内——真 socket 上 400ms 的 Timeout 掐不断一条拖 1.5s 的流（.NET 10 实测）。
        //
        // 所以流式那条路的上限只能来自 CTS，而 Program.cs 把 Timeout 设成 Infinite 的收益在另一头：
        // 非流式走缓冲正文，Timeout 对它是真的有效，那条路的上限已经搬进 ChatCompletionClient 自己
        // （见下一条用例）——两条路的时限从此都在一个地方、都能注入、都能测。
        //
        // 用真 socket 而不是 fake handler：这件事活在传输栈里，假 handler 给不出可信答案。
        await using var server = SlowSseServer.Start(
            (TimeSpan.Zero, DataFrame("""{"choices":[{"delta":{"content":"开头"}}]}""")),
            (TimeSpan.FromMilliseconds(1500), DataFrame("""{"choices":[{"delta":{"content":"结尾"}}]}""")),
            (TimeSpan.Zero, "data: [DONE]\n\n"));
        var client = CreateClient(
            new HttpClientHandler(), baseUrl: server.BaseUrl, httpTimeout: TimeSpan.FromMilliseconds(400));

        var chunks = await DrainAsync(client.CompleteStreamAsync("s", "u"));

        Assert.Equal([LlmChunk.OfContent("开头"), LlmChunk.OfContent("结尾")], chunks);
    }

    [Fact]
    public async Task CompleteStreamAsync_TransportCancelsMidBody_ConvertsToChatCompletionException()
    {
        // 读流中途的 TaskCanceledException（Timeout、代理断链、上游 RST 都长这样）必须收敛成
        // ChatCompletionException：让它冒泡成 OperationCanceledException，编排层就会把它写成与
        // "上游静默"一字不差的一句超时——两种病症长得一样，正是这次排障最贵的那一段。
        var handler = new CapturingHandler((_, ct) => Task.FromResult(SseResponse(
            ct,
            (TimeSpan.Zero, DataFrame("""{"choices":[{"delta":{"content":"开头"}}]}""")),
            (TimeSpan.Zero, CancelMarker))));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(CreateClient(handler).CompleteStreamAsync("s", "u")));

        Assert.Contains("中断", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_ExceedsOwnTimeout_ThrowsChatCompletionException()
    {
        // 非流式（发问/整理）没有"逐块到达"可观测，时限只能自己带——HttpClient.Timeout 已交给 CTS。
        var handler = new CapturingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse(ContentBody);
        });
        var client = CreateClient(handler, nonStreamingTimeout: TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<ChatCompletionException>(() => client.CompleteAsync("s", "u"));

        Assert.Contains("超时", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_CallerCancels_BubblesOperationCanceled()
    {
        var handler = new CapturingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse(ContentBody);
        });
        var client = CreateClient(handler, nonStreamingTimeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync("s", "u", cts.Token));
    }

    [Fact]
    public void Di_ResolvesTypedClient_DespiteOptionalTimeoutParameter()
    {
        // AddHttpClient<T> 用 ActivatorUtilities 造实例：可选的 TimeSpan? 参数（Q4 的可注入时限）
        // 不能把 DI 解析弄坏。这条断言就是"真实启动不炸"的最小复现。
        var services = new ServiceCollection();
        services.AddOptions<RecapOptions>();
        services.AddHttpClient<ChatCompletionClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<ChatCompletionClient>();

        Assert.False(resolved.IsConfigured); // 空配置下解析成功、报"未配置"，正是启动时的状态
    }

    // ---- 脚手架 ----

    private const string ContentBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""";

    private static string DataFrame(string json) => $"data: {json}\n\n";

    /// <summary>脚本里的这一段表示"读流中途被取消"（Timeout / 代理断链在传输层的样子）。</summary>
    private const string CancelMarker = "\u0000cancel";

    private static ChatCompletionClient CreateClient(
        HttpMessageHandler handler,
        string effort = "high",
        TimeSpan? nonStreamingTimeout = null,
        TimeSpan? httpTimeout = null,
        string baseUrl = "https://llm.test/v1")
    {
        var http = new HttpClient(handler) { Timeout = httpTimeout ?? Timeout.InfiniteTimeSpan };
        var options = Options.Create(new RecapOptions
        {
            BaseUrl = baseUrl,
            ApiKey = "sk-test",
            Model = "test-model",
            ReasoningEffort = effort
        });
        return new ChatCompletionClient(http, options, nonStreamingTimeout);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// 头立刻到、正文按脚本慢慢来：真流的时间形状，也是 HttpClient.Timeout 的作案现场。
    ///
    /// 响应体流刻意绑在 SendAsync 收到的 ct 上——真实的 SocketsHttpHandler 就是这么做的，而
    /// HttpClient 传给 handler 的正是"用户 ct + Timeout"的联合 token。少了这一环，fake handler
    /// 会假装 Timeout 掐不到正文，把这次真正踩到的坑测成绿色。
    /// </summary>
    private static HttpResponseMessage SseResponse(CancellationToken ct, params (TimeSpan Delay, string Text)[] parts)
    {
        var content = new StreamContent(new ScriptedStream(parts, ct));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<List<LlmChunk>> DrainAsync(IAsyncEnumerable<LlmChunk> stream)
    {
        var chunks = new List<LlmChunk>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        /// <summary>最后一次请求的 JSON 体：思考参数的映射只能从这里断言。</summary>
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content != null) Body = await request.Content.ReadAsStringAsync(ct);
            return await respond(request, ct);
        }
    }

    /// <summary>
    /// 本地 SSE 服务器：头立刻发、正文按脚本分段发。真 socket 是必需的——HttpClient.Timeout
    /// 对响应体的杀伤力只在真实传输栈里存在（见上面两条用例的注释）。
    /// </summary>
    private sealed class SlowSseServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _serving;

        private SlowSseServer(HttpListener listener, Task serving, string baseUrl)
        {
            _listener = listener;
            _serving = serving;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static SlowSseServer Start(params (TimeSpan Delay, string Text)[] parts)
        {
            // 端口 0 拿一个空闲端口再放掉：HttpListener 不接受 0，这点小竞态换来测试之间不撞端口。
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var serving = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                try
                {
                    foreach (var (delay, text) in parts)
                    {
                        if (delay > TimeSpan.Zero) await Task.Delay(delay);
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
                        await context.Response.OutputStream.FlushAsync();
                    }
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // 客户端先放手（正是"有限 Timeout 掐流"那条用例的预期）：写失败无处可报。
                }
            });

            return new SlowSseServer(listener, serving, $"http://127.0.0.1:{port}/v1");
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (Exception)
            {
                // 监听已停，服务循环怎么结束都无所谓。
            }
            _listener.Close();
        }
    }

    /// <summary>按脚本分段吐字节的响应体，每段之前先等一会。</summary>
    private sealed class ScriptedStream((TimeSpan Delay, string Text)[] parts, CancellationToken transportToken) : Stream
    {
        private int _index;
        private byte[] _current = [];
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset >= _current.Length)
            {
                if (_index >= parts.Length) return 0;
                var (delay, text) = parts[_index++];
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transportToken);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, linked.Token);
                if (text == CancelMarker)
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
                _current = Encoding.UTF8.GetBytes(text);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("流式响应只走异步读。");

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
