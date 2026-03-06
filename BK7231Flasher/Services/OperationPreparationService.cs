using System.Globalization;

namespace BK7231Flasher.Services
{
    class OperationPreparationResult
    {
        public int BaudRate { get; set; }
        public string SerialName { get; set; }
        public int ReadReplyStyle { get; set; }
        public float ReadTimeOutMultForLoop { get; set; }
        public float ReadTimeOutMultForSerialClass { get; set; }
    }

    static class OperationPreparationService
    {
        public static bool TryPrepare(
            string baudRateText,
            bool isSerialSelectionEnabled,
            string selectedSerialName,
            string readTimeOutMultForLoopText,
            string readTimeOutMultForSerialClassText,
            string readReplyStyleText,
            out OperationPreparationResult result,
            out string validationError)
        {
            result = null;
            validationError = null;

            if (!BaudRateParser.TryParse(baudRateText, out int baudRate))
            {
                validationError = "Please enter a correct number for a baud rate.";
                return false;
            }

            string serialName = string.Empty;
            if (isSerialSelectionEnabled)
            {
                serialName = selectedSerialName ?? string.Empty;
                if (serialName.Length <= 0)
                {
                    validationError = "Please choose a correct serial port or connect one if not present.";
                    return false;
                }
            }

            float.TryParse(readTimeOutMultForLoopText.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float readTimeOutMultForLoop);
            float.TryParse(readTimeOutMultForSerialClassText.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float readTimeOutMultForSerialClass);
            int.TryParse(readReplyStyleText.Replace(',', '.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int readReplyStyle);

            result = new OperationPreparationResult
            {
                BaudRate = baudRate,
                SerialName = serialName,
                ReadReplyStyle = readReplyStyle,
                ReadTimeOutMultForLoop = readTimeOutMultForLoop,
                ReadTimeOutMultForSerialClass = readTimeOutMultForSerialClass,
            };
            return true;
        }
    }
}
