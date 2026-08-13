using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.Mac;

/// <summary>Issue 10 落地前的显式不可用 adapter，避免 App-only MVP 假装能更新。</summary>
public sealed class MacNoUpdateController : IUpdateController
{
    public bool IsSupported => false;
    public UpdateSnapshot Current => UpdateSnapshot.Idle;
    public event Action<UpdateSnapshot>? Changed { add { } remove { } }
    public Task<UpdateCheckResult> CheckAsync() => Task.FromResult(UpdateCheckResult.Skipped);
    public Task<bool> ApplyAsync() => Task.FromResult(false);
}
