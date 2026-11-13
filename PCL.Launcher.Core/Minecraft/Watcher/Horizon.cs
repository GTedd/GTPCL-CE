using System.Diagnostics;

namespace PCL;

/// <summary>
///     不经过 <see cref="Watcher" /> 的游戏进程（Horizon）：只跟踪是否在运行，
///     以便与普通实例一样处理启动器可见性与音乐。
/// </summary>
public static partial class GameWatcher
{
    private static int _externalGames;

    /// <summary>
    ///     记录一个已启动的外部游戏进程；进程退出时调用 <paramref name="onExit" /> 并在没有其它游戏时恢复启动器。
    /// </summary>
    public static void ExternalGameStarted(Process process, Action<Process>? onExit = null)
    {
        Interlocked.Increment(ref _externalGames);
        if (!hasRunningMinecraft) MinecraftStart();
        LauncherCommon.RunInNewThread(() =>
        {
            try
            {
                process.WaitForExit();
                LauncherCommon.Log($"[Horizon] 游戏进程已退出，退出码 {process.ExitCode}");
                onExit?.Invoke(process);
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Horizon] 等待游戏进程退出失败");
            }
            finally
            {
                if (Interlocked.Decrement(ref _externalGames) == 0 && hasRunningMinecraft &&
                    !mcWatcherList.Any(watcher => watcher.State is Watcher.MinecraftState.Loading or Watcher.MinecraftState.Running))
                    MinecraftStop(false);
            }
        }, "Horizon Exit Watcher");
    }
}
