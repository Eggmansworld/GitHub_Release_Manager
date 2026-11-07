using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

public sealed record DownloadJob(GhAsset Asset, string Folder, string Tag, bool HashOnly = false)
{
    public string FilePath => Path.Combine(Folder, Asset.Name);
}

public sealed record DownloadProgress(DownloadJob Job, long Downloaded, long Total, double BytesPerSec, double EtaSeconds);

public sealed record DownloadResult(DownloadJob Job, bool Success, string? Sha1, string Error, bool Cancelled, long Bytes = -1)
{
    /// <summary>Real size on disk when known (generated archives), else the size GitHub reported.</summary>
    public long ActualBytes => Bytes >= 0 ? Bytes : Job.Asset.Size;
}

/// <summary>Async gate used for Pause/Resume.</summary>
public sealed class PauseGate
{
    private volatile TaskCompletionSource _tcs = Completed();
    public bool IsPaused { get; private set; }

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;
        _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        _tcs.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);
}

/// <summary>
/// Multi-threaded release downloader with HTTP Range resume and retries (same behaviour as v3).
/// <para>softStop: workers finish their current file and take no new ones (Safe Stop).</para>
/// <para>hardStop: in-flight transfers are aborted; partial files are kept for resume (Hard Stop).</para>
/// </summary>
public sealed class Downloader
{
    public const int RetryLimit = 3;
    private const int ChunkSize = 256 * 1024;

    public event Action<DownloadJob>? Started;
    public event Action<DownloadProgress>? Progress;
    public event Action<DownloadResult>? Completed;

    public async Task RunAsync(IReadOnlyList<DownloadJob> jobs, int threads, PauseGate pause,
        CancellationToken softStop, CancellationToken hardStop)
    {
        var queue = new ConcurrentQueue<DownloadJob>(jobs);
        var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(_ => Task.Run(async () =>
        {
            while (!softStop.IsCancellationRequested && !hardStop.IsCancellationRequested && queue.TryDequeue(out var job))
            {
                try { await pause.WaitAsync(hardStop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (softStop.IsCancellationRequested) break;

                Started?.Invoke(job);
                var result = await DownloadOneAsync(job, pause, hardStop).ConfigureAwait(false);
                Completed?.Invoke(result);
            }
        })).ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task<DownloadResult> DownloadOneAsync(DownloadJob job, PauseGate pause, CancellationToken hardStop)
    {
        if (job.Asset.SizeUnknown) return await DownloadUnknownSizeAsync(job, pause, hardStop).ConfigureAwait(false);
        var path = job.FilePath;
        var total = job.Asset.Size;
        var lastError = "";

        for (var attempt = 0; attempt < RetryLimit; attempt++)
        {
            if (hardStop.IsCancellationRequested)
                return new(job, false, null, "Hard stop requested", true);

            try
            {
                Directory.CreateDirectory(job.Folder);
                long existing = File.Exists(path) ? new FileInfo(path).Length : 0;

                if (existing > 0 && existing == total)
                {
                    // Already complete: just hash it.
                    var done = await Task.Run(() => Hashing.Sha1File(path, hardStop), hardStop).ConfigureAwait(false);
                    return new(job, true, done, "", false);
                }
                if (existing > total) existing = 0; // stale/different file -> restart

                using var req = new HttpRequestMessage(HttpMethod.Get, job.Asset.BrowserDownloadUrl);
                if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

                using var resp = await GitHubApi.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, hardStop).ConfigureAwait(false);
                if (resp.StatusCode != HttpStatusCode.OK && resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException($"HTTP {(int)resp.StatusCode}");

                // Server ignored our Range header -> start the file over.
                if (existing > 0 && resp.StatusCode == HttpStatusCode.OK) existing = 0;

                var downloaded = existing;
                var sw = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;

                await using (var net = await resp.Content.ReadAsStreamAsync(hardStop).ConfigureAwait(false))
                await using (var fs = new FileStream(path, existing > 0 ? FileMode.Append : FileMode.Create,
                                 FileAccess.Write, FileShare.Read, ChunkSize, useAsync: true))
                {
                    var buf = new byte[ChunkSize];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(hardStop);
                    while (true)
                    {
                        idle.CancelAfter(Timeout.InfiniteTimeSpan); // don't let a long pause trip the watchdog
                        await pause.WaitAsync(hardStop).ConfigureAwait(false);
                        idle.CancelAfter(TimeSpan.FromSeconds(60)); // stall watchdog
                        var n = await net.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), hardStop).ConfigureAwait(false);
                        downloaded += n;

                        if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(150))
                        {
                            lastReport = sw.Elapsed;
                            var speed = (downloaded - existing) / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                            var eta = Math.Max(total - downloaded, 0) / Math.Max(speed, 1);
                            Progress?.Invoke(new(job, downloaded, total, speed, eta));
                        }
                    }
                }

                if (total > 0 && downloaded < total)
                    throw new IOException($"Connection closed early ({downloaded}/{total} bytes)");

                Progress?.Invoke(new(job, downloaded, total, 0, 0));
                var sha = await Task.Run(() => Hashing.Sha1File(path, hardStop), hardStop).ConfigureAwait(false);
                return new(job, true, sha, "", false);
            }
            catch (OperationCanceledException) when (hardStop.IsCancellationRequested)
            {
                return new(job, false, null, "Hard stop requested", true);
            }
            catch (Exception ex)
            {
                lastError = ex is OperationCanceledException ? "Transfer stalled (timeout)" : ex.Message;
                try { await Task.Delay(1000, hardStop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new(job, false, null, "Hard stop requested", true); }
            }
        }

        return new(job, false, null, lastError, false);
    }

    /// <summary>
    /// Generated archives (source code / branch snapshots) are built on the fly by GitHub: no size up front
    /// and no resume. They go to a .part file that's renamed once complete.
    /// </summary>
    private async Task<DownloadResult> DownloadUnknownSizeAsync(DownloadJob job, PauseGate pause, CancellationToken hardStop)
    {
        var path = job.FilePath;
        var part = path + ".part";
        var lastError = "";

        if (job.HashOnly && File.Exists(path))
        {
            try
            {
                var sha0 = await Task.Run(() => Hashing.Sha1File(path, hardStop), hardStop).ConfigureAwait(false);
                return new(job, true, sha0, "", false, new FileInfo(path).Length);
            }
            catch (OperationCanceledException) { return new(job, false, null, "Hard stop requested", true); }
            catch (Exception ex) { return new(job, false, null, ex.Message, false); }
        }

        for (var attempt = 0; attempt < RetryLimit; attempt++)
        {
            if (hardStop.IsCancellationRequested) { TryDelete(part); return new(job, false, null, "Hard stop requested", true); }
            try
            {
                Directory.CreateDirectory(job.Folder);
                using var resp = await GitHubApi.Http.GetAsync(job.Asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, hardStop).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new(job, false, null, "HTTP 404 (archive not available)", false);
                if (!resp.IsSuccessStatusCode) throw new IOException($"HTTP {(int)resp.StatusCode}");
                var expected = resp.Content.Headers.ContentLength ?? 0;

                long downloaded = 0;
                var sw = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                await using (var net = await resp.Content.ReadAsStreamAsync(hardStop).ConfigureAwait(false))
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read, ChunkSize, useAsync: true))
                {
                    var buf = new byte[ChunkSize];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(hardStop);
                    while (true)
                    {
                        idle.CancelAfter(Timeout.InfiniteTimeSpan);
                        await pause.WaitAsync(hardStop).ConfigureAwait(false);
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        var n = await net.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        await fs.WriteAsync(buf.AsMemory(0, n), hardStop).ConfigureAwait(false);
                        downloaded += n;
                        if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(150))
                        {
                            lastReport = sw.Elapsed;
                            var speed = downloaded / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                            var eta = expected > 0 ? Math.Max(expected - downloaded, 0) / Math.Max(speed, 1) : 0;
                            Progress?.Invoke(new(job, downloaded, expected, speed, eta));
                        }
                    }
                }
                if (expected > 0 && downloaded < expected)
                    throw new IOException($"Connection closed early ({downloaded}/{expected} bytes)");

                File.Move(part, path, overwrite: true);
                Progress?.Invoke(new(job, downloaded, downloaded, 0, 0));
                var sha = await Task.Run(() => Hashing.Sha1File(path, hardStop), hardStop).ConfigureAwait(false);
                return new(job, true, sha, "", false, downloaded);
            }
            catch (OperationCanceledException) when (hardStop.IsCancellationRequested)
            {
                TryDelete(part);
                return new(job, false, null, "Hard stop requested", true);
            }
            catch (Exception ex)
            {
                lastError = ex is OperationCanceledException ? "Transfer stalled (timeout)" : ex.Message;
                try { await Task.Delay(1000, hardStop).ConfigureAwait(false); }
                catch (OperationCanceledException) { TryDelete(part); return new(job, false, null, "Hard stop requested", true); }
            }
        }
        TryDelete(part);
        return new(job, false, null, lastError, false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
