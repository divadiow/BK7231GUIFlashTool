using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace BK7231Flasher
{
	public class W800Flasher : ECRBaseFlasher, IRomReadFlasher
	{
		const byte CMD_CRC32 = 0x8F;

		public W800Flasher(CancellationToken ct) : base(ct)
		{
		}

		protected override bool doGenericSetup()
		{
			addLog("Now is: " + DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + "." + Environment.NewLine);
			addLog("Flasher mode: " + chipType + Environment.NewLine);
			addLog("Going to open port: " + serialName + "." + Environment.NewLine);
			try
			{
				serial = new SerialPort(serialName, 115200)
				{
					ReadBufferSize = 65536,
					ReadTimeout = 2000
				};
				serial.Open();
				serial.DiscardInBuffer();
				serial.DiscardOutBuffer();
				xm = new XMODEM(serial, XMODEM.Variants.XModem1K, 0xFF)
				{
					SendInactivityTimeoutMillisec = 5000,
					MaxSenderRetries = 5,
					ReceiverTimeoutMillisec = 1000
				};
			}
			catch(Exception ex)
			{
				addLog("Port setup failed with " + ex.Message + "!" + Environment.NewLine);
				return false;
			}
			addLog("Port ready!" + Environment.NewLine);
			return true;
		}

		protected override bool Sync()
		{
			if(ReadFlashId(true) != null)
			{
				addLogLine("Stub is already uploaded!");
				return true;
			}
			if(!SyncW800DownloadMode())
				return false;
			if(!UploadStub())
				return false;
			return ReadFlashId() != null;
		}

		private bool SyncW800DownloadMode()
		{
			int oldReadTimeout = serial.ReadTimeout;
			try
			{
				serial.RtsEnable = false;
				serial.DtrEnable = false;
				serial.DiscardInBuffer();
				serial.DiscardOutBuffer();
				serial.ReadTimeout = 100;
				if(WaitForSyncPrompt(500, false))
					return true;

				addLogLine("W800 sync timeout, sending AT+Z/ESC bootloader entry sequence...");
				serial.RtsEnable = true;
				Thread.Sleep(50);
				byte[] atz = new byte[] { (byte)'A', (byte)'T', (byte)'+', (byte)'Z', 0x0D, 0x0A };
				serial.Write(atz, 0, atz.Length);
				serial.RtsEnable = false;

				byte[] escBurst = new byte[] { 0x1B, 0x1B, 0x1B };
				var count = 0;
				serial.ReadTimeout = 10;
				Stopwatch sw = Stopwatch.StartNew();
				while(sw.ElapsedMilliseconds < 60000 && !isCancelled)
				{
					serial.Write(escBurst, 0, escBurst.Length);
					if(ReadSyncPromptByte(ref count, true))
					{
						serial.DiscardOutBuffer();
						serial.DiscardInBuffer();
						return true;
					}
					Thread.Sleep(10);
				}
				addErrorLine("W800 sync failed: no CCCC download prompt after AT+Z/ESC bootloader entry sequence.");
			}
			catch(Exception ex)
			{
				addErrorLine(ex.Message);
			}
			finally
			{
				try { serial.DiscardOutBuffer(); } catch { }
				try { serial.DiscardInBuffer(); } catch { }
				serial.ReadTimeout = oldReadTimeout;
			}
			return false;
		}

		private bool WaitForSyncPrompt(int timeoutMs, bool preserveTimeoutCount)
		{
			var count = 0;
			int oldReadTimeout = serial.ReadTimeout;
			try
			{
				serial.ReadTimeout = Math.Min(100, timeoutMs);
				Stopwatch sw = Stopwatch.StartNew();
				while(sw.ElapsedMilliseconds < timeoutMs && !isCancelled)
				{
					if(ReadSyncPromptByte(ref count, preserveTimeoutCount))
						return true;
				}
			}
			finally
			{
				serial.ReadTimeout = oldReadTimeout;
			}
			return false;
		}

		private bool ReadSyncPromptByte(ref int count, bool preserveTimeoutCount)
		{
			try
			{
				byte sync = (byte)serial.ReadByte();
				if(sync == 'C')
				{
					count++;
					if(count > 3)
					{
						addLogLine("Sync success!");
						return true;
					}
				}
				else
				{
					count = 0;
				}
			}
			catch(TimeoutException)
			{
				if(!preserveTimeoutCount)
					count = 0;
			}
			return false;
		}

		private bool UploadStub()
		{
			var stub = FLoaders.GetBinaryFromAssembly("W800_Stub");
			addLogLine("Sending stub...");
			if(xm.Send(stub) != stub.Length)
				return false;
			addLogLine("Stub uploaded!");
			if(!WaitForSyncPrompt(20000, true))
				return false;
			return ExecuteCommand(CMD_SYN) != null;
		}

		private bool ReadStubByte(Stopwatch sw, int timeoutMs, out byte value)
		{
			while(sw.ElapsedMilliseconds < timeoutMs && !isCancelled)
			{
				if(serial.BytesToRead > 0)
				{
					value = (byte)serial.ReadByte();
					return true;
				}
				Thread.Sleep(1);
			}
			value = 0;
			return false;
		}

		protected override byte[] ExecuteCommand(int type, byte[] parms = null,
			float timeout = 0.1f, int expectedReplyLen = 0, int br = 115200, bool isErrorExpected = false)
		{
			parms = parms ?? new byte[0];
			var raw = new List<byte>()
			{
				0xA5,
				(byte)type,
				(byte)(parms.Length & 0xFF),
				(byte)((parms.Length >> 8) & 0xFF)
			};
			raw.AddRange(parms);
			raw.Add(StubCRC8(raw.ToArray(), raw.Count));

			serial.DiscardInBuffer();
			serial.Write(raw.ToArray(), 0, raw.Count);
			int timeoutMs = Math.Max(1, (int)(timeout * 1000));
			Stopwatch sw = Stopwatch.StartNew();
			byte value;
			do
			{
				if(!ReadStubByte(sw, timeoutMs, out value))
				{
					if(!isErrorExpected) addErrorLine("Command response is empty!");
					return null;
				}
			} while(value != 0x5A);

			var response = new List<byte>() { value };
			for(int i = 0; i < 3; i++)
			{
				if(!ReadStubByte(sw, timeoutMs, out value))
				{
					if(!isErrorExpected) addErrorLine("Command response header is incomplete!");
					return null;
				}
				response.Add(value);
			}
			int dataLength = response[2] | response[3] << 8;
			for(int i = 0; i < dataLength + 2; i++)
			{
				if(!ReadStubByte(sw, timeoutMs, out value))
				{
					if(!isErrorExpected) addErrorLine("Command response is incomplete!");
					return null;
				}
				response.Add(value);
			}

			byte[] bytes = response.ToArray();
			if(bytes[1] != (byte)type)
			{
				if(!isErrorExpected) addErrorLine($"Command response type 0x{bytes[1]:X2} does not match 0x{type:X2}!");
				return null;
			}
			if(StubCRC8(bytes, bytes.Length - 1) != bytes[bytes.Length - 1])
			{
				addErrorLine("Command checksum is incorrect!");
				logger.setState("Checksum mismatch!", Color.Red);
				return null;
			}
			byte status = bytes[bytes.Length - 2];
			if(status != 0)
			{
				if(!isErrorExpected)
				{
					string statusName = status switch
					{
						0x01 => "ERROR",
						0x02 => "ADDR_ERROR",
						0x03 => "TYPE_ERROR",
						0x04 => "LEN_ERROR",
						0x05 => "CRC_ERROR",
						_ => $"UNKNOWN_ERROR_{status:X2}"
					};
					addErrorLine($"Command status is {statusName}");
				}
				return null;
			}
			if(dataLength != expectedReplyLen)
			{
				if(!isErrorExpected) addErrorLine($"Command reply length {dataLength} != expected {expectedReplyLen}");
				return null;
			}
			if(type == CMD_BAUD)
			{
				serial.BaudRate = br;
				Thread.Sleep(10);
			}
			var ret = new byte[dataLength];
			Array.Copy(bytes, 4, ret, 0, dataLength);
			return ret;
		}

		protected override bool CheckHash(int addr, int len, byte[] data)
		{
			var cmd = new List<byte>();
			cmd.AddRange(BitConverter.GetBytes(addr));
			cmd.AddRange(BitConverter.GetBytes(len));
			byte[] expected = ExecuteCommand(CMD_CRC32, cmd.ToArray(), 30, 4);
			if(expected == null)
				return false;
			uint expectedCrc = BitConverter.ToUInt32(expected, 0);
			uint actualCrc = CRC.crc32_ver2(0xFFFFFFFF, data, len, 0);
			if(actualCrc != expectedCrc)
			{
				addErrorLine($"CRC32 mismatch!\r\ndevice:\t{expectedCrc:X8}\r\nflasher:\t{actualCrc:X8}");
				logger.setState("CRC32 mismatch!", Color.Red);
				return false;
			}
			addSuccess($"CRC32 matches {expectedCrc:X8}!" + Environment.NewLine);
			return true;
		}

		public override bool doErase(int startSector = 0x000, int sectors = 10, bool bAll = false)
		{
			if(!bAll && (startSector < 0x2000 || (startSector & (BK7231Flasher.SECTOR_SIZE - 1)) != 0 || sectors <= 0))
			{
				addErrorLine("W800 erase range must be 0x1000-aligned, in writable flash, and contain at least one sector.");
				return false;
			}
			if(!doGenericSetup() || !Sync())
				return false;

			bool ok;
			if(bAll)
			{
				addLogLine("Erasing writable W800 flash (the protected first 8 KiB is preserved)...");
				ok = ExecuteCommand(CMD_FLASH_CHIPERASE, timeout: 180) != null;
			}
			else
			{
				int length = sectors * BK7231Flasher.SECTOR_SIZE;
				var msg = new List<byte>();
				msg.AddRange(BitConverter.GetBytes(startSector));
				msg.AddRange(BitConverter.GetBytes(length));
				addLogLine($"Erasing W800 flash at 0x{startSector:X}, length 0x{length:X}...");
				ok = ExecuteCommand(CMD_FLASH_ERASE, msg.ToArray(), 60) != null;
			}
			if(ok)
			{
				logger.setState("Erase done", Color.DarkGreen);
				logger.setProgress(1, 1);
				addLogLine("Erase flash ok.");
			}
			else
			{
				logger.setState("Erase error!", Color.Red);
			}
			return ok;
		}

		private bool WriteW800Flash(int address, byte[] data, bool allowUnalignedStart = false)
		{
			int sectorSize = BK7231Flasher.SECTOR_SIZE;
			if(address < 0x2000 || data == null || data.Length == 0)
			{
				addErrorLine("W800 custom stub writes must start in writable flash and contain data.");
				return false;
			}
			if(!allowUnalignedStart && (address & (sectorSize - 1)) != 0)
			{
				addErrorLine("W800 custom stub write addresses must be 0x1000-aligned.");
				return false;
			}
			int alignedAddress = address & ~(sectorSize - 1);
			int prefixLength = address - alignedAddress;
			int alignedLength = (prefixLength + data.Length + sectorSize - 1) & ~(sectorSize - 1);
			if(alignedAddress + alignedLength > flashSizeMB * 0x100000)
			{
				addErrorLine("W800 write range exceeds detected flash size.");
				return false;
			}
			byte[] alignedData = new byte[alignedLength];
			for(int i = 0; i < alignedData.Length; i++)
				alignedData[i] = 0xFF;
			Array.Copy(data, 0, alignedData, prefixLength, data.Length);
			return InternalWrite(alignedAddress, alignedData);
		}

		public override void doWrite(int startSector, byte[] data)
		{
			if(!doGenericSetup() || !Sync())
				return;
			WriteW800Flash(startSector, data);
		}

		public override void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode rwMode)
		{
			if(!doGenericSetup() || !Sync())
				return;
			try
			{
				OBKConfig cfg = rwMode == WriteMode.OnlyOBKConfig ? logger.getConfig() : logger.getConfigToWrite();
				if(rwMode == WriteMode.ReadAndWrite)
				{
					sectors = flashSizeMB * 256;
					addLogLine($"Flash size detected: {flashSizeMB}MB");
					byte[] result = InternalRead(startSector, sectors);
					if(result == null)
						return;
					ms = new MemoryStream(result);
					if(!saveReadResult(startSector))
						return;
				}

				if((rwMode == WriteMode.OnlyWrite || rwMode == WriteMode.ReadAndWrite) && !isCancelled)
				{
					if(string.IsNullOrEmpty(sourceFileName))
					{
						addLogLine("No filename given!");
						return;
					}
					addLogLine("Reading " + sourceFileName + "...");
					byte[] data = File.ReadAllBytes(sourceFileName);
					bool writeOk;
					if(bCustomWriteMode)
					{
						writeOk = WriteW800Flash(startSector, data);
					}
					else if(sourceFileName.EndsWith(".fls", StringComparison.OrdinalIgnoreCase))
					{
						writeOk = WriteW800Fls(data);
					}
					else if(data.Length >= 0x100000)
					{
						startSector = 0x2000;
						if(data[startSector] != 0x9F || data[startSector + 1] != 0xFF ||
							data[startSector + 2] != 0xFF || data[startSector + 3] != 0xA0)
						{
							addErrorLine("Unknown file type, no firmware header at 0x2000!");
							return;
						}
						var cutData = new byte[data.Length - startSector];
						Array.Copy(data, startSector, cutData, 0, cutData.Length);
						writeOk = WriteW800Flash(startSector, cutData);
					}
					else
					{
						addErrorLine("Unknown file type, skipping.");
						return;
					}
					if(!writeOk)
						return;
				}

				if((rwMode == WriteMode.OnlyWrite || rwMode == WriteMode.ReadAndWrite || rwMode == WriteMode.OnlyOBKConfig) && cfg != null && !isCancelled)
				{
					int offset = OBKFlashLayout.getConfigLocation(chipType, out _) - 0x303;
					cfg.saveConfig(chipType);
					var data = new byte[2016 + 0x303];
					MiscUtils.padArray(data, 1);
					Array.Copy(cfg.getData(), 0, data, 0x303, 2016);
					addLog("Now will also write OBK config..." + Environment.NewLine);
					addLog("Long name from CFG: " + cfg.longDeviceName + Environment.NewLine);
					addLog("Short name from CFG: " + cfg.shortDeviceName + Environment.NewLine);
					addLog("Web Root from CFG: " + cfg.webappRoot + Environment.NewLine);
					if(WriteW800Flash(offset, data))
					{
						logger.setState("OBK config write success!", Color.Green);
						logger.setProgress(1, 1);
					}
					else
					{
						logger.setState("OBK config write error!", Color.Red);
					}
				}
				else
				{
					addLog("NOTE: the OBK config writing is disabled, so not writing anything extra." + Environment.NewLine);
				}
			}
			catch(Exception ex)
			{
				addErrorLine(ex.Message);
			}
			finally
			{
				if(!isCancelled) SetBaud(115200);
			}
		}

		private bool WriteW800Fls(byte[] fls)
		{
			const int headerLength = 64;
			const uint flashBase = 0x08000000;
			int cursor = 0;
			int segment = 0;
			while(cursor < fls.Length)
			{
				if(fls.Length - cursor < headerLength || fls[cursor] != 0x9F || fls[cursor + 1] != 0xFF ||
					fls[cursor + 2] != 0xFF || fls[cursor + 3] != 0xA0)
				{
					addErrorLine($"Invalid W800 FLS header at file offset 0x{cursor:X}.");
					return false;
				}
				uint address = BitConverter.ToUInt32(fls, cursor + 8);
				uint length = BitConverter.ToUInt32(fls, cursor + 12);
				uint headerAddress = BitConverter.ToUInt32(fls, cursor + 16);
				if(length == 0 || length > int.MaxValue || length > fls.Length - cursor - headerLength)
				{
					addErrorLine($"Invalid W800 FLS segment length at file offset 0x{cursor:X}.");
					return false;
				}
				uint expectedHeaderCrc = BitConverter.ToUInt32(fls, cursor + 60);
				uint actualHeaderCrc = CRC.crc32_ver2(0xFFFFFFFF, fls, 60, (uint)cursor);
				uint expectedPayloadCrc = BitConverter.ToUInt32(fls, cursor + 24);
				uint actualPayloadCrc = CRC.crc32_ver2(0xFFFFFFFF, fls, (int)length, (uint)(cursor + headerLength));
				if(expectedHeaderCrc != actualHeaderCrc || expectedPayloadCrc != actualPayloadCrc)
				{
					addErrorLine($"W800 FLS CRC mismatch in segment {segment + 1}.");
					return false;
				}
				ulong endAddress = (ulong)address + length;
				ulong flashEnd = (ulong)flashBase + (uint)(flashSizeMB * 0x100000);
				ulong imageLength = (ulong)address - headerAddress + length;
				if(headerAddress < flashBase + 0x2000 || address < (ulong)headerAddress + headerLength ||
					endAddress > flashEnd || imageLength > int.MaxValue)
				{
					addErrorLine($"W800 FLS segment {segment + 1} is outside writable flash.");
					return false;
				}

				int payloadOffset = (int)(address - headerAddress);
				byte[] image = new byte[(int)imageLength];
				for(int i = 0; i < image.Length; i++)
					image[i] = 0xFF;
				Array.Copy(fls, cursor, image, 0, headerLength);
				Array.Copy(fls, cursor + headerLength, image, payloadOffset, (int)length);
				addLogLine($"Writing W800 FLS segment {segment + 1}: header 0x{headerAddress - flashBase:X}, payload 0x{address - flashBase:X}, length 0x{length:X}.");
				if(!WriteW800Flash((int)(headerAddress - flashBase), image, true))
					return false;
				cursor += headerLength + (int)length;
				segment++;
			}
			return segment > 0;
		}

		public byte[] ReadRomTarget(RomReadTarget target)
		{
			try
			{
				if(target == null || target.Kind != RomReadKind.Rom || !target.Address.HasValue || !target.Length.HasValue)
				{
					addErrorLine("Selected W800 ROM read target is not valid.");
					return null;
				}
				if(target.Address.Value < 0 || target.Length.Value <= 0 || target.Address.Value > 0x5000 - target.Length.Value)
				{
					addErrorLine("Selected W800 ROM read range is outside mask ROM.");
					return null;
				}
				if(!doGenericSetup() || !Sync())
					return null;
				return InternalReadRawMemory(target.Address.Value, target.Length.Value, "mask ROM");
			}
			catch(Exception ex)
			{
				addErrorLine("ROM read failed: " + ex.Message);
				return null;
			}
			finally
			{
				try { closePort(); } catch { }
			}
		}

		internal override byte[] ReadMAC()
		{
			if(serial == null || !serial.IsOpen)
				return null;
			return ExecuteCommand(CMD_CUSTOM_GET_MAC, expectedReplyLen: 6, isErrorExpected: true);
		}
	}
}
