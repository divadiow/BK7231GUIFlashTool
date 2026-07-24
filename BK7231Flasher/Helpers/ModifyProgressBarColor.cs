using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BK7231Flasher
{
    public static class ModifyProgressBarColor
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr w, IntPtr l);
        public static void SetState(this ProgressBar pBar, int state)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;

            try
            {
                SendMessage(pBar.Handle, 1040, (IntPtr)state, IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
                // Cosmetic only; leave the default progress-bar appearance.
            }
            catch (EntryPointNotFoundException)
            {
                // Cosmetic only; leave the default progress-bar appearance.
            }
        }
    }
}
