namespace Buckminster;

// The host-to-engine app-lifecycle vocabulary (suspend/constrain on consoles, tab backgrounding on web, focus everywhere): the host observes its platform's notification and calls Engine.NotifyLifecycle; modules and the game subscribe to Engine.Lifecycle. Per-window focus events stay in the window event stream -- these are process-level.
public enum LifecycleEvent
{
    Suspended,
    Resumed,
    FocusGained,
    FocusLost,
}
