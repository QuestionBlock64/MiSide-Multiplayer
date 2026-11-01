namespace MiSideMultiplayer.Runtime.Login
{
    /// <summary>
    /// Any stage that needs to be polled each frame while it is the active
    /// stage implements this. The controller checks for it in Tick().
    /// Mirrors the Unreal controller calling ExecuteLoadingStage on the
    /// active stage.
    /// </summary>
    public interface ILoginStagePollable
    {
        void Poll(float dt);
    }
}