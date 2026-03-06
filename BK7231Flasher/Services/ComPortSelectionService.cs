using System;

namespace BK7231Flasher.Services
{
    static class ComPortSelectionService
    {
        public static bool ArePortsSame(string[] previousPorts, string[] newPorts)
        {
            if (previousPorts == null || newPorts == null)
            {
                return false;
            }

            if (previousPorts.Length != newPorts.Length)
            {
                return false;
            }

            for (int i = 0; i < previousPorts.Length; i++)
            {
                if (!string.Equals(previousPorts[i], newPorts[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public static int ResolveSelectedIndex(string previousSelectedPort, string[] ports)
        {
            if (ports == null || ports.Length == 0)
            {
                return -1;
            }

            for (int i = 0; i < ports.Length; i++)
            {
                if (string.Equals(previousSelectedPort, ports[i], StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return ports.Length - 1;
        }
    }
}
