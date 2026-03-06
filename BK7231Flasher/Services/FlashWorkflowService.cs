namespace BK7231Flasher.Services
{
    class BackupAndFlashRequest
    {
        public BKType ChipType { get; set; }
        public string BackupName { get; set; }
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

        static int GetBackupStartSectorForPlatform(BKType chipType)
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

        static int GetBackupSectorCountForPlatform()
        {
            return BK7231Flasher.FLASH_SIZE / BK7231Flasher.SECTOR_SIZE;
        }
    }
}
