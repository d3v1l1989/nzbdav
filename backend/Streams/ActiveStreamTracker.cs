using System.Collections.Concurrent;
using System.Diagnostics;
using NzbWebDAV.Config;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Streams;

public class ActiveStreamTracker
{
    private readonly ConcurrentDictionary<string, ActiveStreamInfo> _activeStreams = new();
    private readonly ConfigManager _configManager;
    private readonly WebsocketManager _websocketManager;
    private readonly Timer _broadcastTimer;

    public ActiveStreamTracker(ConfigManager configManager, WebsocketManager websocketManager)
    {
        _configManager = configManager;
        _websocketManager = websocketManager;
        _broadcastTimer = new Timer(_ => BroadcastActiveStreams(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public ActiveStreamInfo? Register(string fileName)
    {
        if (!_configManager.IsActiveStreamTrackerEnabled()) return null;
        var info = new ActiveStreamInfo(fileName);
        _activeStreams[info.Id] = info;
        BroadcastActiveStreams();
        return info;
    }

    public void Deregister(string id)
    {
        _activeStreams.TryRemove(id, out _);
        BroadcastActiveStreams();
    }

    private void BroadcastActiveStreams()
    {
        if (!_configManager.IsActiveStreamTrackerEnabled() && _activeStreams.IsEmpty) return;
        try
        {
            var streams = _activeStreams.Values
                .Select(s => s.ToMessage())
                .ToArray();
            var message = string.Join(";", streams);
            _websocketManager.SendMessage(WebsocketTopic.ActiveStreams, message);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to broadcast active streams");
        }
    }
}

public class ActiveStreamInfo
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string FileName { get; }
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);
    public int ActiveConnections => _activeConnections;

    private long _bytesTransferred;
    private int _activeConnections;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _lastBytes;
    private double _lastElapsedMs;
    private double _speedBytesPerSecond;
    private readonly object _syncLock = new();

    public ActiveStreamInfo(string fileName)
    {
        FileName = fileName;
    }

    public void AddBytes(int count)
    {
        Interlocked.Add(ref _bytesTransferred, count);
    }

    public void IncrementConnections()
    {
        Interlocked.Increment(ref _activeConnections);
    }

    public void DecrementConnections()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    public string ToMessage()
    {
        var bytes = BytesTransferred;
        long speed;

        lock (_syncLock)
        {
            var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
            var intervalMs = elapsed - _lastElapsedMs;
            var intervalBytes = bytes - _lastBytes;

            if (intervalMs > 500)
            {
                _speedBytesPerSecond = intervalBytes / (intervalMs / 1000.0);
                _lastBytes = bytes;
                _lastElapsedMs = elapsed;
            }

            speed = (long)_speedBytesPerSecond;
        }

        // format: id|fileName|bytesTransferred|speedBytesPerSec|activeConnections
        var safeName = FileName.Replace('|', '_');
        return $"{Id}|{safeName}|{bytes}|{speed}|{ActiveConnections}";
    }
}
