using InGameDevTools.Utils;
using System.Diagnostics.CodeAnalysis;
using Vintagestory.API.Common;

namespace InGameDevTools.Integration;

public sealed class ObjectCache<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _mapping = [];
    private readonly Dictionary<TKey, long> _lastAccess = [];
    private readonly ReaderWriterLockSlim _lock = new();
    private ICoreAPI? _api;
    private readonly int _cleanUpPeriodMs;
    private readonly long _cleanUpTimer = 0;
    private readonly int _cleanUpThreshold;
    private readonly string _loggedCacheName;
    private readonly bool _threadSafe;
    private long _addCountBetweenCleanUps = 0;
    private long _getCountBetweenCleanUps = 0;

    public ObjectCache(ICoreAPI api, string loggedCacheName = "cache", int cleanUpThreshold = 100, int cleanUpPeriod = 10 * 60 * 1000, bool threadSafe = true)
    {
        _api = api;
        _cleanUpThreshold = cleanUpThreshold;
        _cleanUpPeriodMs = cleanUpPeriod;
        _loggedCacheName = loggedCacheName;
        _threadSafe = threadSafe;
        _cleanUpTimer = api.World.RegisterGameTickListener(_ => Clean(), _cleanUpPeriodMs, _cleanUpPeriodMs);
    }

    public void Add(TKey key, TValue value)
    {
        EnterWriteLock();
        try
        {
            _addCountBetweenCleanUps++;
            _mapping[key] = value;
            _lastAccess[key] = CurrentTime();
        }
        finally
        {
            ExitWriteLock();
        }
    }

    public bool Get(TKey key, [NotNullWhen(true)] out TValue? value)
    {
        EnterReadLock();
        bool success;
        try
        {
            success = _mapping.TryGetValue(key, out value);
        }
        finally
        {
            ExitReadLock();
        }

        EnterWriteLock();
        try
        {
            _getCountBetweenCleanUps++;
            if (success && _mapping.ContainsKey(key))
            {
                _lastAccess[key] = CurrentTime();
            }
        }
        finally
        {
            ExitWriteLock();
        }

        return success;
    }

    public void Clean()
    {
        long currentTime = CurrentTime();
        EnterWriteLock();

        try
        {
            LoggerUtil.Verbose(_api, this, $"({_loggedCacheName}) Starting clean up. Current world time: {TimeSpan.FromMilliseconds(currentTime)}\nSize: {_mapping.Count}\n'Get' count: {_getCountBetweenCleanUps}\n'Add' count: {_addCountBetweenCleanUps}");
            _getCountBetweenCleanUps = 0;
            _addCountBetweenCleanUps = 0;

            HashSet<TKey> keysToRemove = [];
            HashSet<TValue> entities = [];
            foreach ((TKey key, long lastAccess) in _lastAccess)
            {
                if (currentTime - lastAccess > _cleanUpPeriodMs)
                {
                    keysToRemove.Add(key);
                    entities.Add(_mapping[key]);
                }
            }

            foreach (TKey key in keysToRemove)
            {
                _mapping.Remove(key);
                _lastAccess.Remove(key);
            }

            LoggerUtil.Verbose(_api, this, $"({_loggedCacheName}) Cleaned up '{keysToRemove.Count}' keys for '{entities.Count}' values.");
        }
        catch (Exception exception)
        {
            LoggerUtil.Error(_api, this, $"({_loggedCacheName}) Error on cache cleanup:\n{exception}");
        }
        finally
        {
            ExitWriteLock();
        }
    }

    public void Clear()
    {
        EnterWriteLock();
        try
        {
            _mapping.Clear();
            _lastAccess.Clear();
        }
        finally
        {
            ExitWriteLock();
        }
    }

    public void Dispose()
    {
        EnterWriteLock();
        try
        {
            _mapping.Clear();
            _lastAccess.Clear();
            _api?.World.UnregisterGameTickListener(_cleanUpTimer);
            _api = null;
        }
        finally
        {
            ExitWriteLock();
            _lock.Dispose();
        }
    }

    private long CurrentTime() => _api?.World.ElapsedMilliseconds ?? 0;

    private void EnterReadLock()
    {
        if (_threadSafe) _lock.EnterReadLock();
    }

    private void ExitReadLock()
    {
        if (_threadSafe) _lock.ExitReadLock();
    }

    private void EnterWriteLock()
    {
        if (_threadSafe) _lock.EnterWriteLock();
    }

    private void ExitWriteLock()
    {
        if (_threadSafe) _lock.ExitWriteLock();
    }
}
