using System;
using System.Runtime.InteropServices;

namespace ArcademiaGameLauncher.Utils
{
    public static class FullscreenDetector
    {
        private enum QueryUserNotificationState
        {
            NotPresent = 1,
            Busy = 2,
            RunningDirect3DFullScreen = 3,
            PresentationMode = 4,
            AcceptsNotifications = 5,
            QuietTime = 6,
            App = 7,
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

        public static bool IsExclusiveFullscreen()
        {
            try
            {
                return SHQueryUserNotificationState(out var state) == 0
                    && state == QueryUserNotificationState.RunningDirect3DFullScreen;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
