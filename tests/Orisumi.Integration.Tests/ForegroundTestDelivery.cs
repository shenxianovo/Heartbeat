using Orisumi.Observer.Sdk;
using Orisumi.Observers.ForegroundState;

namespace Orisumi.Integration.Tests;

// Drive the host's sender explicitly so business repair tests have no timer races.
internal sealed class ForegroundTestDelivery(ObserverClient sdk, LocalApplicationRegistry applications,
    ForegroundRegistration registration, TextWriter output)
{
    private readonly ForegroundDelivery _business = new(new LocalObserverSession(sdk), applications, registration, output);
    internal async Task SendAsync(CancellationToken token)
    {
        await sdk.SendAsync(token);
        await _business.SendAsync(token);
        await sdk.SendAsync(token);
    }
}
