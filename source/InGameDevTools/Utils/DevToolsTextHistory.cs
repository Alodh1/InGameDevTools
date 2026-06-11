namespace InGameDevTools.Utils;

/// <summary>
/// Undo/redo history for an editor text buffer. Call <see cref="Record"/> every frame with the
/// live text: identical text is a no-op, rapid edits coalesce into one step, and edits after an
/// undo truncate the redo branch. Reset when a different document is loaded.
/// </summary>
internal sealed class DevToolsTextHistory(int capacity = 40, double coalesceSeconds = 0.75)
{
    private readonly List<string> _states = [];
    private int _index = -1;
    private double _lastRecordTime = double.NegativeInfinity;

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _states.Count - 1;
    public string Current => _index >= 0 ? _states[_index] : "";

    public void Reset(string text)
    {
        _states.Clear();
        _states.Add(text);
        _index = 0;
        _lastRecordTime = double.NegativeInfinity;
    }

    public void Record(string text, double now)
    {
        if (_states.Count == 0)
        {
            Reset(text);
            return;
        }

        if (string.Equals(text, _states[_index], StringComparison.Ordinal)) return;

        if (_index < _states.Count - 1)
        {
            _states.RemoveRange(_index + 1, _states.Count - _index - 1);
        }

        bool coalesce = now - _lastRecordTime < coalesceSeconds && _index > 0;
        if (coalesce)
        {
            _states[_index] = text;
        }
        else
        {
            _states.Add(text);
            _index++;
        }

        if (_states.Count > capacity)
        {
            int drop = _states.Count - capacity;
            _states.RemoveRange(0, drop);
            _index -= drop;
        }

        _lastRecordTime = now;
    }

    public bool TryUndo(out string text)
    {
        text = Current;
        if (!CanUndo) return false;

        _index--;
        text = _states[_index];
        // The next edit must start a new step instead of coalescing into the restored state.
        _lastRecordTime = double.NegativeInfinity;
        return true;
    }

    public bool TryRedo(out string text)
    {
        text = Current;
        if (!CanRedo) return false;

        _index++;
        text = _states[_index];
        _lastRecordTime = double.NegativeInfinity;
        return true;
    }
}
