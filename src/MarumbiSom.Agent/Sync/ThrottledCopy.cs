namespace MarumbiSom.Agent.Sync;

/// <summary>Cópia de um download com limite de banda (seção 8.3) e desistência quando nada chega por um tempo.</summary>
internal static class ThrottledCopy
{
    /// <param name="bytesPerSecond">0 = sem limite.</param>
    /// <param name="progress">Recebe o total copiado nesta chamada a cada bloco.</param>
    public static async Task CopyAsync(
        Stream source, Stream destination, double bytesPerSecond, TimeSpan stallTimeout, TimeProvider time, Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var started = time.GetTimestamp();
        long total = 0;
        while (true)
        {
            int read;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                stall.CancelAfter(stallTimeout);
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Download parado há {stallTimeout.TotalSeconds:0} s.");
                }
            }

            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Invoke(total);
            if (bytesPerSecond > 0)
            {
                var expected = TimeSpan.FromSeconds(total / bytesPerSecond);
                var elapsed = time.GetElapsedTime(started);
                if (expected > elapsed)
                {
                    await Task.Delay(expected - elapsed, time, cancellationToken);
                }
            }
        }
    }
}
