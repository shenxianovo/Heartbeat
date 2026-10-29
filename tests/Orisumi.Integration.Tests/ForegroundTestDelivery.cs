using Orisumi.Observer.Runtime.Store;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

// Drive the host's sender explicitly so business repair tests have no timer races.
internal sealed class ForegroundTestDelivery(ObserverStore runtimeStore, LocalApplicationRegistry applications,
    ForegroundRegistration registration, TextWriter output)
{
    private readonly ForegroundDelivery _business = new(new LocalObserverSession(runtimeStore), applications, registration, output);
    internal async Task SendAsync(CancellationToken token)
    {
        await runtimeStore.SendAsync(token);
        await _business.SendAsync(token);
        await runtimeStore.SendAsync(token);
    }
}
