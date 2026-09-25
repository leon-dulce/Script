namespace FlowKey.Core;

public sealed class SessionState
{
    public string Mode { get; private set; } = "ready";

    public bool BeginRecording()
    {
        if (Mode is not ("ready" or "paused")) return false;
        Mode = "recording";
        return true;
    }

    public bool PauseRecording()
    {
        if (Mode != "recording") return false;
        Mode = "paused";
        return true;
    }

    public bool FinishRecording()
    {
        if (Mode is not ("recording" or "paused")) return false;
        Mode = "ready";
        return true;
    }

    public bool BeginRun()
    {
        if (Mode != "ready") return false;
        Mode = "running";
        return true;
    }

    public bool StopRun()
    {
        if (Mode != "running") return false;
        Mode = "ready";
        return true;
    }
}
