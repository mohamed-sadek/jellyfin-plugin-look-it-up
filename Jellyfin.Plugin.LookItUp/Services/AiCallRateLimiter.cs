namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Simple global AI call pacing (shared across prepare jobs).
/// </summary>
public interface IAiCallRateLimiter
{
    /// <summary>
    /// Waits until another AI call is allowed under the RPM budget.
    /// </summary>
    Task WaitTurnAsync(int maxCallsPerMinute, CancellationToken cancellationToken);
}

/// <summary>
/// Token-bucket style minute window limiter.
/// </summary>
public sealed class AiCallRateLimiter : IAiCallRateLimiter
{
    private readonly object _gate = new();
    private readonly Queue<DateTime> _stamps = new();

    /// <inheritdoc />
    public async Task WaitTurnAsync(int maxCallsPerMinute, CancellationToken cancellationToken)
    {
        if (maxCallsPerMinute <= 0)
        {
            maxCallsPerMinute = 30;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? delay = null;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                while (_stamps.Count > 0 && now - _stamps.Peek() > TimeSpan.FromMinutes(1))
                {
                    _stamps.Dequeue();
                }

                if (_stamps.Count < maxCallsPerMinute)
                {
                    _stamps.Enqueue(now);
                    return;
                }

                var oldest = _stamps.Peek();
                delay = TimeSpan.FromMinutes(1) - (now - oldest) + TimeSpan.FromMilliseconds(50);
            }

            if (delay is { } d && d > TimeSpan.Zero)
            {
                await Task.Delay(d, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
