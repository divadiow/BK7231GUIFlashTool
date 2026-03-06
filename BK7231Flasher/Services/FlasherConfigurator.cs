namespace BK7231Flasher.Services
{
    static class FlasherConfigurator
    {
        public static void Configure(
            BaseFlasher flasher,
            ILogListener logListener,
            string serialName,
            BKType chipType,
            int baudRate,
            int readReplyStyle,
            float readTimeoutMultForLoop,
            float readTimeoutMultForSerialClass,
            bool overwriteBootloader,
            bool skipKeyCheck,
            bool ignoreCrcErrors)
        {
            flasher.setBasic(logListener, serialName, chipType, baudRate);
            flasher.setReadReplyStyle(readReplyStyle);
            flasher.setReadTimeOutMultForLoop(readTimeoutMultForLoop);
            flasher.setReadTimeOutMultForSerialClass(readTimeoutMultForSerialClass);
            flasher.setOverwriteBootloader(overwriteBootloader);
            flasher.setSkipKeyCheck(skipKeyCheck);
            flasher.setIgnoreCRCErr(ignoreCrcErrors);
        }
    }
}
