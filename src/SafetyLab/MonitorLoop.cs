using System.Diagnostics;
using System.Text.Json;

namespace SafetyLab;

/// <summary>
/// The sampling schedule, shared by every backend's monitor. Only the "read the server" part is
/// backend-specific; the pacing, the periodic meta re-emit and the never-die-on-a-blip error
/// handling are properties of the capture and must not drift between arms — each of the three has
/// already cost this rig a wrong answer once, and a second copy is a second chance to lose one of
/// them.
/// </summary>
public abstract class MonitorLoop
{
    private readonly TimeSpan _tick;
    private readonly TextWriter _out;
    private long _seq;

    /// <summary>Roughly once a minute at a 1s tick — cheap, and it makes any capture self-describing.</summary>
    private const int MetaEveryNSamples = 60;

    protected MonitorLoop(TimeSpan tick, TextWriter output)
    {
        _tick = tick;
        _out = output;
    }

    /// <summary>The meta record for this capture, probed once at startup.</summary>
    protected abstract Task<MetaRecord> DescribeAsync(CancellationToken token);

    /// <summary>
    /// Read one tick of server-side truth. <c>ElapsedMs</c> is overwritten by the caller, so an
    /// implementation can leave it at zero.
    /// </summary>
    protected abstract Task<Sample> TakeSampleAsync(long seq, DateTimeOffset started, CancellationToken token);

    /// <summary>Drop any cached connection after a failed tick. Default: nothing to drop.</summary>
    protected virtual Task OnSampleFailedAsync() => Task.CompletedTask;

    public async Task RunAsync(CancellationToken token)
    {
        var meta = await DescribeAsync(token);
        WriteLine(meta);

        // PeriodicTimer rather than a delay loop: a sample that takes 300ms must not push the
        // whole schedule out by 300ms. A tick missed because the previous one overran shows up
        // as a gap in `seq` vs wall-clock, which the coverage checker reports.
        using var timer = new PeriodicTimer(_tick);

        while (!token.IsCancellationRequested)
        {
            // Re-emit meta periodically. The monitor pod is long-lived and captures attach with
            // `kubectl logs --since`, so a capture that starts mid-life would otherwise never see
            // the one meta line written at startup -- and a history without meta loses the declared
            // tick and lock id, which is how the first live run reported 1,900 phantom sampling
            // gaps against an assumed default tick.
            if (_seq % MetaEveryNSamples == 0)
            {
                WriteLine(meta);
            }

            await sampleOnceAsync(token);

            try
            {
                if (!await timer.WaitForNextTickAsync(token)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task sampleOnceAsync(CancellationToken token)
    {
        var seq = Interlocked.Increment(ref _seq);
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sample = await TakeSampleAsync(seq, started, token);
            WriteLine(sample with { ElapsedMs = stopwatch.Elapsed.TotalMilliseconds });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception e)
        {
            // Record the hole rather than dying. A monitor that exits on the first blip stops
            // watching exactly when the cluster gets interesting -- and a DB outage is itself a
            // scenario worth having in the history, not a reason to lose the run.
            await OnSampleFailedAsync();
            WriteLine(new Sample(seq, started, null, stopwatch.Elapsed.TotalMilliseconds, [], [], [],
                $"{e.GetType().Name}: {e.Message}"));
        }
    }

    protected void WriteLine<T>(T record)
    {
        _out.WriteLine(JsonSerializer.Serialize(record, Json.Options));
        _out.Flush();
    }
}
