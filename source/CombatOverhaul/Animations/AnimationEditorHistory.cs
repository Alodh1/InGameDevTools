#if DEBUG
using System.Collections.Generic;

namespace CombatOverhaul.Animations;

internal sealed class AnimationEditorHistory
{
    private const int MaxEntriesPerAnimation = 100;

    private readonly Dictionary<string, List<AnimationHistoryEntry>> _undo = new();
    private readonly Dictionary<string, List<AnimationHistoryEntry>> _redo = new();
    private PendingAnimationEdit? _pendingEdit;

    public int UndoCount(string animationCode) => GetStack(_undo, animationCode).Count;
    public int RedoCount(string animationCode) => GetStack(_redo, animationCode).Count;
    public bool HasPendingEdit(string animationCode) => _pendingEdit?.AnimationCode == animationCode;

    public void BeginEdit(string animationCode, Animation animation, string label)
    {
        if (_pendingEdit?.AnimationCode == animationCode) return;

        if (_pendingEdit != null)
        {
            CancelPendingEdit();
        }

        _pendingEdit = new PendingAnimationEdit(animationCode, AnimationHistoryEntry.FromAnimation(label, animation));
    }

    public bool CommitEdit(string animationCode, Animation animation)
    {
        if (_pendingEdit?.AnimationCode != animationCode) return false;

        AnimationHistoryEntry entry = _pendingEdit.Before;
        _pendingEdit = null;

        string current = Serialize(animation);
        if (current == entry.Serialized) return false;

        Push(_undo, animationCode, entry);
        GetStack(_redo, animationCode).Clear();
        return true;
    }

    public void CancelPendingEdit()
    {
        _pendingEdit = null;
    }

    public bool RecordSnapshot(string animationCode, Animation before, string label)
    {
        AnimationHistoryEntry entry = AnimationHistoryEntry.FromAnimation(label, before);
        List<AnimationHistoryEntry> undo = GetStack(_undo, animationCode);
        if (undo.Count > 0 && undo[^1].Serialized == entry.Serialized) return false;

        Push(_undo, animationCode, entry);
        GetStack(_redo, animationCode).Clear();
        return true;
    }

    public bool Undo(string animationCode, IDictionary<string, Animation> animations, out string status)
    {
        status = "";
        if (!animations.TryGetValue(animationCode, out Animation? current))
        {
            status = $"Undo failed: {animationCode} is not loaded.";
            return false;
        }

        List<AnimationHistoryEntry> undo = GetStack(_undo, animationCode);
        if (undo.Count == 0)
        {
            status = "Nothing to undo.";
            return false;
        }

        AnimationHistoryEntry target = Pop(undo);
        Push(_redo, animationCode, AnimationHistoryEntry.FromAnimation("Redo", current));
        animations[animationCode] = target.ToAnimation();
        status = $"Undid {target.Label}.";
        return true;
    }

    public bool Redo(string animationCode, IDictionary<string, Animation> animations, out string status)
    {
        status = "";
        if (!animations.TryGetValue(animationCode, out Animation? current))
        {
            status = $"Redo failed: {animationCode} is not loaded.";
            return false;
        }

        List<AnimationHistoryEntry> redo = GetStack(_redo, animationCode);
        if (redo.Count == 0)
        {
            status = "Nothing to redo.";
            return false;
        }

        AnimationHistoryEntry target = Pop(redo);
        Push(_undo, animationCode, AnimationHistoryEntry.FromAnimation("Undo", current));
        animations[animationCode] = target.ToAnimation();
        status = $"Redid {target.Label}.";
        return true;
    }

    public void Clear(string animationCode)
    {
        GetStack(_undo, animationCode).Clear();
        GetStack(_redo, animationCode).Clear();
        if (_pendingEdit?.AnimationCode == animationCode) _pendingEdit = null;
    }

    private static void Push(Dictionary<string, List<AnimationHistoryEntry>> stacks, string animationCode, AnimationHistoryEntry entry)
    {
        List<AnimationHistoryEntry> stack = GetStack(stacks, animationCode);
        stack.Add(entry);
        if (stack.Count > MaxEntriesPerAnimation)
        {
            stack.RemoveRange(0, stack.Count - MaxEntriesPerAnimation);
        }
    }

    private static AnimationHistoryEntry Pop(List<AnimationHistoryEntry> stack)
    {
        int index = stack.Count - 1;
        AnimationHistoryEntry entry = stack[index];
        stack.RemoveAt(index);
        return entry;
    }

    private static List<AnimationHistoryEntry> GetStack(Dictionary<string, List<AnimationHistoryEntry>> stacks, string animationCode)
    {
        if (!stacks.TryGetValue(animationCode, out List<AnimationHistoryEntry>? stack))
        {
            stack = new();
            stacks[animationCode] = stack;
        }

        return stack;
    }

    internal static string Serialize(Animation animation) => AnimationJson.FromAnimation(animation).ToString();

    private sealed record PendingAnimationEdit(string AnimationCode, AnimationHistoryEntry Before);

    private sealed class AnimationHistoryEntry
    {
        private readonly Animation _animation;
        private readonly int _playerFrameIndex;
        private readonly int _itemFrameIndex;
        private readonly int _soundsFrameIndex;
        private readonly int _particlesFrameIndex;
        private readonly int _callbackFrameIndex;
        private readonly float _frameProgress;

        private AnimationHistoryEntry(string label, Animation animation)
        {
            Label = label;
            _animation = animation.Clone();
            _playerFrameIndex = animation._playerFrameIndex;
            _itemFrameIndex = animation._itemFrameIndex;
            _soundsFrameIndex = animation._soundsFrameIndex;
            _particlesFrameIndex = animation._particlesFrameIndex;
            _callbackFrameIndex = animation._callbackFrameIndex;
            _frameProgress = animation._frameProgress;
            Serialized = Serialize(animation);
        }

        public string Label { get; }
        public string Serialized { get; }

        public static AnimationHistoryEntry FromAnimation(string label, Animation animation) => new(label, animation);

        public Animation ToAnimation()
        {
            Animation animation = _animation.Clone();
            animation._playerFrameIndex = _playerFrameIndex;
            animation._itemFrameIndex = _itemFrameIndex;
            animation._soundsFrameIndex = _soundsFrameIndex;
            animation._particlesFrameIndex = _particlesFrameIndex;
            animation._callbackFrameIndex = _callbackFrameIndex;
            animation._frameProgress = _frameProgress;
            return animation;
        }
    }
}
#endif
