using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Heartbeat.Hub.Tests;

public sealed class RecordUploaderTests : IDisposable
{
    private readonly QueueFixture _fixture = new();

    [Fact]
    public async Task OfflineSubmissionResolvesItsRouteAfterRestartAndThenUploads()
    {
        var record = QueueFixture.Snapshot();
        _fixture.Open().Accept(QueueFixture.Submission(record));
        using (var offline = new HttpClient(new Handler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")))))
        {
            Assert.Single(await new RecordUploader(_fixture.Open(), offline, _fixture.Token).UploadOnceAsync());
        }

        using var backend = new MappedHandler((_, _) => Task.FromResult(Receipt(record)));
        using var client = new HttpClient(backend);
        Assert.Empty(await new RecordUploader(_fixture.Open(), client, _fixture.Token).UploadOnceAsync());
        Assert.Equal(1, backend.RegistrationRequests);
        Assert.Equal(1, backend.TrackRequests);
        Assert.Equal(1, backend.UploadRequests);
        Assert.Equal(new QueueStatus(0, 0), _fixture.Open().Status());
    }

    [Fact]
    public async Task OfflineAccumulationIsSplitIntoBoundedUploadBodies()
    {
        var queue = _fixture.Open();
        for (var index = 0; index < 30; index++)
        {
            queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot() with
            {
                Value = JsonSerializer.SerializeToElement(new { text = new string('a', 50000) }),
            }));
        }

        using var handler = new MappedHandler(async (request, token) =>
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            Assert.True(bytes.Length <= RecordOutbox.MaximumBatchBytes, $"Upload body contained {bytes.Length} bytes.");
            var body = JsonSerializer.Deserialize<UploadBatch>(bytes, JsonSerializerOptions.Web)!;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    results = body.Records.Select((record, index) => new
                    {
                        index, record.Id, status = "stored", record.EndedAt, receivedAt = DateTimeOffset.UtcNow,
                    }),
                }),
            };
        });
        using var client = new HttpClient(handler);
        await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync();
        Assert.True(handler.UploadRequests > 1);
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public async Task BackendReceiptAtPostgresMicrosecondPrecisionConfirmsTheSnapshot()
    {
        var queue = _fixture.Open();
        var original = QueueFixture.Snapshot();
        var record = original with { EndedAt = original.EndedAt!.Value.AddTicks(7) };
        queue.Accept(QueueFixture.Submission(record));
        using var handler = new MappedHandler((_, _) => Task.FromResult(Receipt(original with { Id = record.Id })));
        using var client = new HttpClient(handler);
        Assert.Empty(await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync());
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public async Task PointReceiptWithNullEndConfirmsTheSnapshot()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot() with { EndedAt = null, Value = JsonSerializer.SerializeToElement(42) };
        queue.Accept(new HubSubmission(QueueFixture.Collector(),
            new TrackDeclaration("example.point", 1, "point", null), [record]));
        using var handler = new MappedHandler((_, _) => Task.FromResult(Receipt(record)));
        using var client = new HttpClient(handler);

        Assert.Empty(await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync());
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public async Task LostBackendReceiptKeepsOriginalIdentityAndPersistedMappingForRetry()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot();
        queue.Accept(QueueFixture.Submission(record));
        var saved = new HashSet<Guid>();
        var calls = 0;
        using var handler = new MappedHandler(async (request, token) =>
        {
            var body = await request.Content!.ReadFromJsonAsync<UploadBatch>(token);
            saved.Add(Assert.Single(body!.Records).Id);
            if (++calls == 1)
            {
                throw new HttpRequestException("Response lost after backend commit");
            }

            return Receipt(record);
        });
        using var client = new HttpClient(handler);
        Assert.Single(await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync());
        Assert.Equal(1, queue.Status().Pending);
        Assert.Empty(await new RecordUploader(_fixture.Open(), client, _fixture.Token).UploadOnceAsync());
        Assert.Single(saved);
        Assert.Equal(1, handler.RegistrationRequests);
        Assert.Equal(1, handler.TrackRequests);
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public async Task InFlightResponseConfirmsOnlyTheSentSnapshot()
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot();
        queue.Accept(QueueFixture.Submission(record));
        using var handler = new MappedHandler((_, _) =>
        {
            _fixture.Open().Accept(QueueFixture.Submission(record with
            {
                EndedAt = record.EndedAt!.Value.AddMinutes(1),
            }));
            return Task.FromResult(Receipt(record));
        });
        using var client = new HttpClient(handler);
        await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync();
        Assert.Equal(record.EndedAt!.Value.AddMinutes(1), Assert.Single(queue.TakePending()).Record.EndedAt);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("duplicate-index")]
    [InlineData("missing-index")]
    [InlineData("short-end")]
    [InlineData("unknown-status")]
    [InlineData("missing-results")]
    public async Task MalformedReceiptNeverDeletesData(string defect)
    {
        var queue = _fixture.Open();
        var record = QueueFixture.Snapshot();
        queue.Accept(QueueFixture.Submission(record));
        object item = new
        {
            index = defect == "missing-index" ? (int?)null : 0,
            id = defect == "wrong-id" ? Guid.NewGuid() : record.Id,
            status = defect == "unknown-status" ? "accepted" : "stored",
            endedAt = defect == "short-end" ? record.StartedAt : record.EndedAt,
            receivedAt = DateTimeOffset.UtcNow,
        };
        using var handler = new MappedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                results = defect == "missing-results" ? [] : defect == "duplicate-index" ? new[] { item, item } : [item],
            }),
        }));
        using var client = new HttpClient(handler);
        Assert.Single(await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync());
        Assert.Equal(new QueueStatus(1, 0), queue.Status());
    }

    [Fact]
    public async Task PartialSuccessParksOnlyPermanentFailuresAndOtherRoutesStillUpload()
    {
        var queue = _fixture.Open();
        queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot(), QueueFixture.Snapshot()));
        queue.Accept(new HubSubmission(QueueFixture.Collector(),
            new TrackDeclaration("other.data", 1, "range", "explicit"), [QueueFixture.Snapshot()]));
        using var handler = new MappedHandler(async (request, token) =>
        {
            var body = await request.Content!.ReadFromJsonAsync<UploadBatch>(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    results = body!.Records.Select((record, index) => new
                    {
                        index, record.Id, status = index == 1 ? "conflict" : "stored",
                        record.EndedAt, receivedAt = DateTimeOffset.UtcNow,
                    }).Reverse(),
                }),
            };
        });
        using var client = new HttpClient(handler);
        var uploader = new RecordUploader(queue, client, _fixture.Token);
        Assert.Empty(await uploader.UploadOnceAsync());
        Assert.Equal(new QueueStatus(0, 1), _fixture.Open().Status());
        Assert.Equal("conflict", Assert.Single(queue.ReadFailures()).Failure);
        Assert.Empty(await uploader.UploadOnceAsync());
    }

    [Theory]
    [InlineData(401, "unauthorized", false)]
    [InlineData(429, "too_many_requests", false)]
    [InlineData(500, "error", false)]
    [InlineData(404, "proxy_not_found", false)]
    [InlineData(404, "track_not_found", true)]
    [InlineData(400, "invalid_request", true)]
    [InlineData(400, "unsupported_protocol", false)]
    [InlineData(409, "track_protocol_conflict", false)]
    public async Task OnlyRecognizedPermanentRecordFailuresSuspendRecords(int status, string code, bool permanent)
    {
        var queue = _fixture.Open();
        queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot()));
        using var handler = new MappedHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = JsonContent.Create(new { code }),
        }));
        using var client = new HttpClient(handler);
        await new RecordUploader(queue, client, _fixture.Token).UploadOnceAsync();
        Assert.Equal(permanent ? new QueueStatus(0, 1) : new QueueStatus(1, 0), queue.Status());
    }

    [Fact]
    public void WrongTokenOwnerIsRejectedBeforeAnyUpload()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() =>
            new RecordUploader(_fixture.Open(), client, QueueFixture.TokenFor(Guid.NewGuid())));
    }

    private static HttpResponseMessage Receipt(RecordSnapshot record) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            results = new[] { new { index = 0, record.Id, status = "stored", record.EndedAt, receivedAt = DateTimeOffset.UtcNow } },
        }),
    };

    private sealed record UploadBatch(IReadOnlyList<RecordSnapshot> Records);

    private sealed class MappedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> upload)
        : HttpMessageHandler
    {
        private readonly Guid _collectorId = Guid.CreateVersion7();
        private readonly Guid _trackId = Guid.CreateVersion7();

        public int RegistrationRequests { get; private set; }
        public int TrackRequests { get; private set; }
        public int UploadRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/collectors")
            {
                RegistrationRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = _collectorId,
                        key = QueueFixture.Collector().Key,
                        target = QueueFixture.Collector().Target,
                    }),
                });
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/tracks", StringComparison.Ordinal))
            {
                TrackRequests++;
                return ResolveTrack(request, cancellationToken);
            }

            UploadRequests++;
            return upload(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> ResolveTrack(HttpRequestMessage request, CancellationToken token)
        {
            var declaration = await request.Content!.ReadFromJsonAsync<TrackDeclaration>(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = _trackId,
                    collectorId = _collectorId,
                    declaration!.Type,
                    declaration.Version,
                    declaration.TimeMode,
                    declaration.EndMode,
                }),
            };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    public void Dispose() => _fixture.Dispose();
}
