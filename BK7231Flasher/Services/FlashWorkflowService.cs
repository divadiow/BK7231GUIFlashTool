namespace BK7231Flasher.Services
{
    class BackupAndFlashRequest
    {
        public BKType ChipType { get; set; }
        public string BackupName { get; set; }
        public string SourceFile { get; set; }
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
