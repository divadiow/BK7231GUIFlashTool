using System;
using System.Threading;

namespace BK7231Flasher.Services
{
    static class FlasherFactory
    {
        public static BaseFlasher Create(BKType chipType, CancellationToken token)
        {
            switch (chipType)
            {
                case BKType.RTL8710B:
                case BKType.RTL8720D:
                case BKType.RTL8721DA:
                case BKType.RTL8720E:
                    return new RTLFlasher(token);
                case BKType.RTL87X0C:
                    return new RTLZ2Flasher(token);
                case BKType.LN882H:
                case BKType.LN8825:
                    return new LN882HFlasher(token);
                case BKType.BL602:
                case BKType.BL702:
                    return new BL602Flasher(token);
                case BKType.BekenSPI:
                    return new SPIFlasher_Beken(token);
                case BKType.GenericSPI:
                    return new SPIFlasher(token);
                case BKType.ECR6600:
                    return new ECR6600Flasher(token);
                case BKType.W600:
                case BKType.W800:
                    return new WMFlasher(token);
                case BKType.RDA5981:
                    return new RDAFlasher(token);
                case BKType.ESP32:
                case BKType.ESP32S3:
                case BKType.ESP32C3:
                case BKType.ESP8266:
                    return new ESPFlasher(token);
                default:
                    return new BK7231Flasher(token);
            }
        }
    }
}
