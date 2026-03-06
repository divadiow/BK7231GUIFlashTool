using System.Collections.Generic;

namespace BK7231Flasher
{
    static class ChipCatalog
    {
        public static readonly IReadOnlyDictionary<BKType, string> Chips = new Dictionary<BKType, string>
        {
            { BKType.BK7231T,    "BK7231T" },
            { BKType.BK7231U,    "BK7231U" },
            { BKType.BK7231N,    "BK7231N (T2, T34)" },
            { BKType.BK7231M,    "BK7231M" },
            { BKType.BK7236,     "BK7236 (T3)" },
            { BKType.BK7238,     "BK7238 (T1)" },
            { BKType.BK7252,     "BK7252" },
            { BKType.BK7252N,    "BK7252N (T4)" },
            { BKType.BK7258,     "BK7258 (T5)" },
            { BKType.RTL8710B,   "RTL8710B (AmebaZ)" },
            { BKType.RTL87X0C,   "RTL87X0C (AmebaZ2)" },
            { BKType.RTL8720D,   "RTL8720DN (AmebaD)" },
            { BKType.LN882H,     "LN882H" },
            { BKType.LN8825,     "LN8825" },
            { BKType.BL602,      "BL602" },
            { BKType.BL702,      "BL702" },
            { BKType.ECR6600,    "ECR6600" },
            { BKType.W800,       "W800" },
            { BKType.W600,       "W600 (write)" },
            { BKType.RDA5981,    "RDA5981" },
            { BKType.BekenSPI,   "Beken SPI CH341" },
            { BKType.GenericSPI, "Generic SPI CH341" },
            { BKType.ESP32,      "ESP32" },
            { BKType.ESP32S3,    "ESP32-S3" },
            { BKType.ESP32C3,    "ESP32-C3" },
            { BKType.ESP8266,    "ESP8266" },
        };

        public static readonly int[] BaudRates = { 115200, 230400, 460800, 921600, 1500000, 2000000, 3000000 };
    }
}
