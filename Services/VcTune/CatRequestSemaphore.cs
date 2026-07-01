namespace Yaesu_Web_Control.Services;

/// <summary>
/// Singleton wrapper around the shared CAT-pipeline SemaphoreSlim(1,1).
/// Injected into both <see cref="Controllers.CatController"/> and
/// <see cref="VcTuneService"/> so all HTTP-triggered CAT operations share
/// one serialisation lock over the radio's serial port.
/// Message 48 updates CatController to inject this instead of its static field.
/// </summary>
public sealed class CatRequestSemaphore
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Asynchronously waits to enter the semaphore with the given timeout.
    /// </summary>
    /// <param name="timeoutMs">Milliseconds to wait before giving up.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>True if the semaphore was acquired; false if it timed out.</returns>
    public Task<bool> WaitAsync(int timeoutMs, CancellationToken cancellationToken = default)
        => _semaphore.WaitAsync(timeoutMs, cancellationToken);

    /// <summary>Releases the semaphore once.</summary>
    public void Release() => _semaphore.Release();
}
