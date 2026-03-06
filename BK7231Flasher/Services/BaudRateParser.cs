using System;
using System.Globalization;

namespace BK7231Flasher.Services
{
    static class BaudRateParser
    {
        public static bool TryParse(string value, out int baudRate)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out baudRate)
                && baudRate > 0;
        }
    }
}
