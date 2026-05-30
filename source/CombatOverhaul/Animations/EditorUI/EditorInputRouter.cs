#if DEBUG
namespace CombatOverhaul.Animations.EditorUI;

internal sealed class EditorInputRouter
{
    public EditorInputRouter()
    {
    }

    public bool CapturesInput { get; private set; }

    public void SetActive(bool active)
    {
        CapturesInput = active;
    }
}
#endif
