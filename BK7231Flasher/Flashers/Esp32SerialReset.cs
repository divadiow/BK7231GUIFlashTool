using System;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BK7231Flasher
{
    /// <summary>
    /// Implements esptool-style ESP32 reset strategies and the Windows usbser.sys control-line workaround,
    /// while keeping platform-specific interop out of ESPFlasher.cs.
    /// </summary>
    internal sealed class Esp32SerialReset
    {
        private SerialPort _serial;

        private bool _dtrState = false;
        private bool _rtsState = false;

        private bool _haveWin32Handle = false;
        private IntPtr _comHandle = IntPtr.Zero;

        // EscapeCommFunction() constants
        private const uint SETRTS = 3;
        private const uint CLRRTS = 4;
        private const uint SETDTR = 5;
        private const uint CLRDTR = 6;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EscapeCommFunction(IntPtr hFile, uint dwFunc);

        /// <summary>
        /// Bind this helper to the current SerialPort instance.
        /// Safe to call repeatedly (eg after re-open/re-enumeration).
        /// </summary>
        public void Bind(SerialPort serial)
        {
            _serial = serial;
            RefreshComHandle();
        }

        /// <summary>
        /// Try to obtain a Win32 handle for the serial device so we can force DTR/RTS updates
        /// even when .NET SerialPort optimises away "no-op" assignments.
        /// </summary>
        public void RefreshComHandle()
        {
            _haveWin32Handle = false;
            _comHandle = IntPtr.Zero;

            if (_serial == null)
                return;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                if (!_serial.IsOpen)
                    return;

                var bs = _serial.BaseStream;
                if (bs == null)
                    return;

                SafeFileHandle sfh = null;
                var t = bs.GetType();

                // SerialStream has an internal SafeFileHandle (commonly "_handle") depending on runtime.
                var prop = t.GetProperty("SafeFileHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    sfh = prop.GetValue(bs, null) as SafeFileHandle;
                }

                if (sfh == null)
                {
                    var fld = t.GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? t.GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fld != null)
                    {
                        sfh = fld.GetValue(bs) as SafeFileHandle;
                    }
                }

                if (sfh != null && !sfh.IsInvalid)
                {
                    _comHandle = sfh.DangerousGetHandle();
                    _haveWin32Handle = _comHandle != IntPtr.Zero;
                }
            }
            catch
            {
                _haveWin32Handle = false;
                _comHandle = IntPtr.Zero;
            }
        }

        public void SetNeutral()
        {
            SetDTR(false);
            SetRTS(false);
        }

        public void SetDTR(bool state)
        {
            _dtrState = state;

            if (_serial == null)
                return;

            if (_haveWin32Handle)
            {
                EscapeCommFunction(_comHandle, state ? SETDTR : CLRDTR);
                return;
            }

            try { _serial.DtrEnable = state; } catch { }
        }

        public void SetRTS(bool state)
        {
            _rtsState = state;

            if (_serial == null)
                return;

            if (_haveWin32Handle)
            {
                EscapeCommFunction(_comHandle, state ? SETRTS : CLRRTS);

                // esptool workaround for usbser.sys on Windows:
                // after changing RTS, re-send the current DTR state so the updated RTS is applied.
                EscapeCommFunction(_comHandle, _dtrState ? SETDTR : CLRDTR);
                return;
            }

            try
            {
                _serial.RtsEnable = state;

                // Best-effort fallback for the same usbser.sys quirk:
                // forcing any DTR assignment may trigger a SET_CONTROL_LINE_STATE request.
                _serial.DtrEnable = _serial.DtrEnable;
            }
            catch { }
        }

        /// <summary>
        /// esptool ClassicReset sequence: D0|R1|W0.1|D1|R0|W0.05|D0
        /// </summary>
        public void ClassicReset(int resetDelayMs = 50)
        {
            SetDTR(false);   // IO0=HIGH
            SetRTS(true);    // EN=LOW, chip in reset
            Thread.Sleep(100);
            SetDTR(true);    // IO0=LOW
            SetRTS(false);   // EN=HIGH, chip out of reset
            Thread.Sleep(resetDelayMs);
            SetDTR(false);   // IO0=HIGH, done
        }

        /// <summary>
        /// esptool USBJTAGSerialReset sequence for USB-Serial-JTAG ports (ESP32-S3/C3/etc).
        /// </summary>
        public void USBJTAGSerialReset()
        {
            SetRTS(false);
            SetDTR(false);   // Idle
            Thread.Sleep(100);

            SetDTR(true);    // Set IO0
            SetRTS(false);
            Thread.Sleep(100);

            // Reset. Calls inverted to go through (1,1) instead of (0,0)
            SetRTS(true);
            SetDTR(false);
            SetRTS(true);    // RTS set as Windows only propagates DTR on RTS setting
            Thread.Sleep(100);

            SetDTR(false);
            SetRTS(false);   // Chip out of reset
        }
    }
}
