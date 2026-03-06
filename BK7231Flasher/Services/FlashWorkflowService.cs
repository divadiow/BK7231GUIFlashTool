namespace BK7231Flasher.Services
{
    class BackupAndFlashRequest
    {
        public BKType ChipType { get; set; }
        public string BackupName { get; set; }
        public string SourceFile { get; set; }
    }


    class ReadRequest
    {
        public BKType ChipType { get; set; }
        public int? CustomOffset { get; set; }
        public int? CustomLength { get; set; }
    }

    class ReadResolvedRequest
    {
        public int StartSector { get; set; }
        public int Sectors { get; set; }
        public bool IsFullRead { get; set; }
        public bool RequiresBk7252Notice { get; set; }
    }

    class WriteOnlyRequest
    {
        public BKType ChipType { get; set; }
        public int? CustomOffset { get; set; }
        public int? CustomLength { get; set; }
        public string CustomSourceFile { get; set; }
        public string DefaultSourceFile { get; set; }
    }

    class WriteOnlyResolvedRequest
    {
        public int StartSector { get; set; }
        public int Sectors { get; set; }
        public string SourceFile { get; set; }
    }

    static class FlashWorkflowService
    {
        public static void RunBackupAndFlash(BaseFlasher flasher, BackupAndFlashRequest request)
        {
            flasher.setBackupName(request.BackupName);

            int startSector = GetBackupStartSectorForPlatform(request.ChipType);
            int sectors = GetBackupSectorCountForPlatform();
            flasher.doReadAndWrite(startSector, sectors, request.SourceFile, WriteMode.ReadAndWrite);
        }

        public static WriteOnlyResolvedRequest ResolveWriteOnlyRequest(WriteOnlyRequest request)
        {
            if (request.CustomOffset.HasValue && request.CustomLength.HasValue)
            {
                return new WriteOnlyResolvedRequest
                {
                    StartSector = request.CustomOffset.Value,
                    Sectors = request.CustomLength.Value / BK7231Flasher.SECTOR_SIZE,
                    SourceFile = request.CustomSourceFile,
                };
            }

            return new WriteOnlyResolvedRequest
            {
                StartSector = GetBackupStartSectorForPlatform(request.ChipType),
                Sectors = GetBackupSectorCountForPlatform(),
                SourceFile = request.DefaultSourceFile,
            };
        }

        public static ReadResolvedRequest ResolveReadRequest(ReadRequest request)
        {
            if (request.CustomOffset.HasValue && request.CustomLength.HasValue)
            {
                int startSector = request.CustomOffset.Value;
                if (request.ChipType == BKType.RTL8720D || request.ChipType == BKType.RTL87X0C || request.ChipType == BKType.RTL8710B)
                {
                    startSector /= BK7231Flasher.SECTOR_SIZE;
                }

                return new ReadResolvedRequest
                {
                    StartSector = startSector,
                    Sectors = request.CustomLength.Value / BK7231Flasher.SECTOR_SIZE,
                    IsFullRead = false,
                    RequiresBk7252Notice = false,
                };
            }

            if (request.ChipType == BKType.BK7252)
            {
                int startSector = 0x11000;
                return new ReadResolvedRequest
                {
                    StartSector = startSector,
                    Sectors = GetBackupSectorCountForPlatform() - (startSector / BK7231Flasher.SECTOR_SIZE),
                    IsFullRead = true,
                    RequiresBk7252Notice = true,
                };
            }

            return new ReadResolvedRequest
            {
                StartSector = 0,
                Sectors = GetBackupSectorCountForPlatform(),
                IsFullRead = true,
                RequiresBk7252Notice = false,
            };
        }

        public static int GetBackupStartSectorForPlatform(BKType chipType)
        {
            switch (chipType)
            {
                case BKType.BK7231T:
                case BKType.BK7231U:
                case BKType.BK7252:
                    return BK7231Flasher.BOOTLOADER_SIZE;
                default:
                    return 0;
            }
        }

        public static int GetBackupSectorCountForPlatform()
        {
            return BK7231Flasher.FLASH_SIZE / BK7231Flasher.SECTOR_SIZE;
        }
    }
}
