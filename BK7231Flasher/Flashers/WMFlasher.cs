using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace BK7231Flasher
{
	public class WMFlasher : BaseFlasher, IRomReadFlasher
	{
		// it uses CRC16 CCITT 0xFFFF
		// WM command - 0x21 {length high} {length low} {crc16 high} {crc16 low} {command 4 bytes} {payload}
		MemoryStream ms;
		int flashSizeMB = 2;
		byte[] flashID;
		const byte CMD_STUB_SYNC = 0x00;
		const byte CMD_STUB_FLASH_ERASE = 0x04;
		const byte CMD_STUB_FLASH_CHIP_ERASE = 0x05;
		const byte CMD_STUB_BAUD = 0x07;
		const byte CMD_STUB_CRC32 = 0x8F;
		const byte CMD_STUB_FLASH_ID = 0x90;
		const byte CMD_STUB_XMODEM_WRITE = 0x91;
		const byte CMD_STUB_XMODEM_READ = 0x92;
		const byte CMD_STUB_GET_MAC = 0x95;
		const byte CMD_STUB_XMODEM_READ_COMPRESSED = 0x96;
		const byte CMD_STUB_XMODEM_WRITE_COMPRESSED = 0x97;
		const byte CMD_STUB_XMODEM_READ_RAW = 0x98;

		public WMFlasher(CancellationToken ct) : base(ct)
		{
		}

		bool doGenericSetup()
		{
			addLog("Now is: " + DateTime.Now.ToLongDateString() + " " + DateTime.Now.ToLongTimeString() + "." + Environment.NewLine);
			addLog("Flasher mode: " + chipType + Environment.NewLine);
			addLog("Going to open port: " + serialName + "." + Environment.NewLine);
			try
			{
				serial = new SerialPort(serialName, 115200);
				if(chipType == BKType.W800)
				{
					// W800 RAM-stub reads return 4096 bytes + CRC at high baud.
					// Use a larger receive buffer so the host does not lose data while draining.
					serial.ReadBufferSize = 65536;
				}
				serial.Open();
				serial.DiscardInBuffer();
				serial.DiscardOutBuffer();
				serial.ReadTimeout = 2000;
				xm = new XMODEM(serial, XMODEM.Variants.XModem1K, 0xFF)
				{
					SendInactivityTimeoutMillisec = 5000,
					MaxSenderRetries = 5
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

		public byte[] ReadFlashId()
		{
			var res = ExecuteCommand(0x3c, null, 1, 10, 0, true);
			if(res != null && res[0] == 'F' && res[1] == 'I' && res[2] == 'D')
			{
				if(chipType == BKType.W600)
				{
					flashID = new byte[]
					{
						Convert.ToByte($"{(char)res[4]}{(char)res[5]}", 16),
					};
					addLogLine($"Flash ID: 0x{flashID[0]:X}");
				}
				else
				{
					flashID = new byte[]
					{
						Convert.ToByte($"{(char)res[4]}{(char)res[5]}", 16),
						Convert.ToByte($"{(char)res[7]}{(char)res[8]}", 16),
					};
					addLogLine($"Flash ID: 0x{flashID[0]:X}xx{flashID[1]:X}");
					flashSizeMB = (1 << (flashID[1] - 0x11)) / 8;
					addLogLine($"Flash size is {flashSizeMB}MB");
				}
			}
			else if(chipType == BKType.W600)
			{
				addLogLine($"Getting flash id failed, assuming device is in secboot mode.");
				addLogLine($"Erasing secboot, will resync...");
				ExecuteCommand(0x3f, null, 1, 2);
				Sync();
				return ReadFlashId();
			}
			if(chipType != BKType.W600)
			{
				var romv = ExecuteCommand(0x3e, null, 1, 3);
				addLogLine($"ROM version: {(char)romv[2]}");
			}
			return flashID;
		}

		public bool Sync()
		{
			serial.DiscardInBuffer();
			var count = 0;
			try
			{
				int attempts = 0;
				while(attempts++ < 1000)
				{
					byte sync = 0;
					try
					{
						sync = (byte)serial.ReadByte();
					}
					catch { }
					if(sync == 'C')
						count++;
					else
					{
						if(chipType == BKType.W600)
						{
							if(sync == 'P')
								continue;
							for(int i = 0; i < 250; i++)
							{
								serial.Write(new byte[] { 0x1B }, 0, 1);
								Thread.Sleep(1);
							}
						}
						else
							Thread.Sleep(250);
						addLogLine($"Sync attempt {attempts}/1000 failed...");
						serial.DiscardInBuffer();
						count = 0;
					}
					if(count > 3)
					{
						addLogLine($"Sync success!");
						return true;
					}
				}
			}
			catch(Exception ex) { addErrorLine(ex.Message); }
			return false;
		}

		private bool InitialSync()
		{
			if(chipType != BKType.W800)
				return Sync();

			return SyncW800DownloadMode();
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
				if(WaitForW800SyncPrompt(500))
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
					if(ReadW800SyncPromptByte(ref count, true))
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

		private bool WaitForW800SyncPrompt(int timeoutMs)
		{
			var count = 0;
			Stopwatch sw = Stopwatch.StartNew();
			while(sw.ElapsedMilliseconds < timeoutMs)
			{
				if(ReadW800SyncPromptByte(ref count, false))
					return true;
			}
			return false;
		}

		private bool ReadW800SyncPromptByte(ref int count, bool preserveTimeoutCount)
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
			if(chipType == BKType.W600) return true;
			var stub = FLoaders.GetBinaryFromAssembly("W800_Stub");
			addLogLine($"Sending stub...");
			if(xm.Send(stub) == stub.Length)
			{
				addLogLine($"Stub uploaded!");
				if(!Sync())
					return false;
				return ExecuteStubCommand(CMD_STUB_SYNC) != null;
			}
			return false;
		}

		private bool InitialiseTarget()
		{
			if(chipType == BKType.W800 && ReadStubFlashId(true) != null)
			{
				addLogLine("Stub is already uploaded!");
				return true;
			}
			if(!InitialSync())
				return false;
			if(chipType == BKType.W600)
				return ReadFlashId() != null && UploadStub();
			return UploadStub() && ReadStubFlashId() != null;
		}

		private byte StubChecksum(byte[] data, int length)
		{
			byte checksum = 0;
			unchecked
			{
				for(int i = 0; i < length; i++)
					checksum += data[i];
			}
			return checksum;
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

		private byte[] ExecuteStubCommand(byte type, byte[] parms = null,
			float timeout = 0.2f, int expectedReplyLen = 0, int newBaud = 0, bool isErrorExpected = false)
		{
			parms = parms ?? new byte[0];
			var raw = new List<byte>()
			{
				0xA5,
				type,
				(byte)(parms.Length & 0xFF),
				(byte)((parms.Length >> 8) & 0xFF)
			};
			raw.AddRange(parms);
			raw.Add(StubChecksum(raw.ToArray(), raw.Count));

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
			if(bytes[1] != type)
			{
				if(!isErrorExpected) addErrorLine($"Command response type 0x{bytes[1]:X2} does not match 0x{type:X2}!");
				return null;
			}
			if(StubChecksum(bytes, bytes.Length - 1) != bytes[bytes.Length - 1])
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
			if(newBaud > 0)
			{
				serial.BaudRate = newBaud;
				Thread.Sleep(10);
			}
			var ret = new byte[dataLength];
			Array.Copy(bytes, 4, ret, 0, dataLength);
			return ret;
		}

		private byte[] ReadStubFlashId(bool isErrorExpected = false)
		{
			byte[] id = ExecuteStubCommand(CMD_STUB_FLASH_ID, expectedReplyLen: 4, isErrorExpected: isErrorExpected);
			if(id == null)
				return null;
			flashID = new byte[] { id[0], id[1], id[2] };
			addLogLine($"Flash ID: 0x{flashID[0]:X2}{flashID[1]:X2}{flashID[2]:X2}");
			if(flashID[2] < 0x11 || flashID[2] > 0x1C)
				throw new Exception("Flash ID incorrect!");
			flashSizeMB = (1 << (flashID[2] - 0x11)) / 8;
			addLogLine($"Flash size is {flashSizeMB}MB");
			return flashID;
		}

		public override void doWrite(int startSector, byte[] data)
		{
			return;
		}

		byte[] ExecuteCommand(int type, byte[] parms = null,
			float timeout = 0.1f, int expectedReplyLen = 0, int br = 115200, bool isErrorExpected = false)
		{
			parms = parms ?? (new byte[0]);
			var cmd = new List<byte>()
			{
				(byte)(type & 0xFF),
				(byte)((type >> 8) & 0xFF),
				(byte)((type >> 16) & 0xFF),
				(byte)((type >> 24) & 0xFF),
			};
			cmd.AddRange(parms);
			var raw = new List<byte>()
			{ 
				0x21, 
				(byte)(cmd.Count + 2 & 0xFF), 
				(byte)((cmd.Count + 2 >> 8) & 0xFF)
			};
			var crc = CRC16.Compute(CRC16Type.CCITT_FALSE, cmd.ToArray());
			raw.Add((byte)(crc & 0xFF));
			raw.Add((byte)((crc >> 8) & 0xFF));
			raw.AddRange(cmd);

			serial.DiscardInBuffer();
			serial.Write(raw.ToArray(), 0, raw.Count);
			if(type == 0x31)
			{
				Thread.Sleep(10);
				serial.BaudRate = br;
			}
			int timeoutMS = (int)(timeout * 1000);
			if(type == 0x4A && expectedReplyLen > 1024)
				return ReadLargeCommandResponse(type, expectedReplyLen, timeoutMS, isErrorExpected);

			Stopwatch sw = Stopwatch.StartNew();
			while(sw.ElapsedMilliseconds < timeoutMS)
			{
				if(serial.BytesToRead >= expectedReplyLen)
					break;
			}
			if(serial.BytesToRead == 0)
			{
				if(!isErrorExpected)
					addErrorLine("Command response is empty!");
				return null;
			}
			var bytes = new byte[serial.BytesToRead];
			serial.Read(bytes, 0, bytes.Length);
			if(bytes.Length < expectedReplyLen)
			{
				if(!isErrorExpected)
					addErrorLine($"Command reply length {bytes.Length} < expected {expectedReplyLen}");
				return null;
			}
			var ret = new byte[expectedReplyLen];
			Array.Copy(bytes, 0, ret, 0, expectedReplyLen);
			return ret;
		}

		private byte[] ReadLargeCommandResponse(int type, int expectedReplyLen, int timeoutMS, bool isErrorExpected)
		{
			var ret = new byte[expectedReplyLen];
			int offset = 0;
			Stopwatch sw = Stopwatch.StartNew();
			while(sw.ElapsedMilliseconds < timeoutMS && offset < expectedReplyLen && !isCancelled)
			{
				int available = serial.BytesToRead;
				if(available > 0)
				{
					int wanted = Math.Min(available, expectedReplyLen - offset);
					offset += serial.Read(ret, offset, wanted);
					continue;
				}
				Thread.Sleep(1);
			}

			if(offset == 0)
			{
				if(!isErrorExpected)
					addErrorLine("Command response is empty!");
				else if(type == 0x4a)
					addWarningLine($"Command 0x4A response is empty at {serial.BaudRate} baud.");
				return null;
			}

			if(offset < expectedReplyLen)
			{
				if(!isErrorExpected)
					addErrorLine($"Command reply length {offset} < expected {expectedReplyLen}");
				else if(type == 0x4a)
				{
					int previewLen = Math.Min(offset, 32);
					var preview = new byte[previewLen];
					Array.Copy(ret, preview, previewLen);
					addWarningLine($"Command 0x4A short streamed reply {offset}/{expectedReplyLen} at {serial.BaudRate} baud: {BitConverter.ToString(preview)}{(offset > previewLen ? " ..." : string.Empty)}");
				}
				return null;
			}

			if(serial.BytesToRead > 0)
				serial.DiscardInBuffer();
			return ret;
		}

		private bool SetBaud(int baud, bool noResync = false)
		{
			if(chipType == BKType.W800)
			{
				if(serial.BaudRate == baud)
					return true;
				if(baud != 115200 && baud != 230400 && baud != 460800 && baud != 921600 &&
					baud != 1000000 && baud != 1250000 && baud != 1500000 && baud != 2000000)
				{
					addErrorLine($"W800 custom stub does not support {baud} baud.");
					return false;
				}
				addLogLine($"Changing baud to {baud}...");
				var stubMsg = BitConverter.GetBytes(baud);
				return ExecuteStubCommand(CMD_STUB_BAUD, stubMsg, 2, 0, baud) != null;
			}
			addLogLine($"Changing baud to {baud}{(!noResync ? ", will resync..." : string.Empty)}");
			var msg = new byte[4];
			msg[0] = (byte)(baud & 0xFF);
			msg[1] = (byte)((baud >> 8) & 0xFF);
			msg[2] = (byte)((baud >> 16) & 0xFF);
			msg[3] = (byte)((baud >> 24) & 0xFF);
			ExecuteCommand(0x31, msg, 1, 1, baud, noResync);
			return noResync || Sync();
		}

		private bool EraseAndWait(string label, byte[] parms, int timeoutSeconds)
		{
			addLogLine(label);
			var response = ExecuteCommand(0x32, parms, timeoutSeconds, 4);
			return response != null && response.Length >= 4
				&& response[0] == 'C' && response[1] == 'C' && response[2] == 'C' && response[3] == 'C';
		}

		public bool ReadFlash(MemoryStream stream, int offset, int size)
		{
			if(chipType == BKType.W800)
			{
				byte[] data = ReadStubMemory(offset, size, false, "flash");
				if(data == null)
					return false;
				stream.Write(data, 0, data.Length);
				return true;
			}
			var readLength = 4096;
			int count = (size + readLength - 1) / readLength;
			int crcErrCount = 0;
			int respErrCount = 0;

			logger.setProgress(0, count);
			logger.setState("Reading...", Color.Transparent);
			for(int i = 0; i < count; i++)
			{
				int displayOffset = offset >= 0x08000000 ? offset ^ 0x08000000 : offset;
				addLog(string.Format($"Read block at 0x{displayOffset:X6}..."));
				var header = new byte[8];
				header[0] = (byte)(offset & 0xFF);
				header[1] = (byte)((offset >> 8) & 0xFF);
				header[2] = (byte)((offset >> 16) & 0xFF);
				header[3] = (byte)((offset >> 24) & 0xFF);
				header[4] = (byte)(readLength & 0xFF);
				header[5] = (byte)((readLength >> 8) & 0xFF);
				header[6] = (byte)((readLength >> 16) & 0xFF);
				header[7] = (byte)((readLength >> 24) & 0xFF);
				var response = ExecuteCommand(0x4a, header, 2, readLength + 4, isErrorExpected: true);
				if(response == null)
				{
					addWarningLine("Failed to get response! Retrying...");
					if(respErrCount++ > 10)
					{
						addErrorLine("Response error count exceeded limit, stopping!");
						return false;
					}
					i--;
					continue;
				}
				else
				{
					respErrCount = 0;
				}

				var crc32 = CRC.crc32_ver2(0xFFFFFFFF, response, response.Length - 4);
				var recvcrc32 = BitConverter.ToUInt32(response, response.Length - 4);
				if(crc32 != recvcrc32)
				{
					addWarningLine("CRC Error! Retrying...");
					if(crcErrCount++ > 10)
					{
						addErrorLine("CRC error count exceeded limit, stopping!");
						return false;
					}
					i--;
					continue;
				}
				else
				{
					crcErrCount = 0;
				}
				stream.Write(response, 0, response.Length - 4);
				offset += readLength;
				logger.setProgress(i, count);
			}

			logger.setProgress(count, count);
			addLog("All blocks read!" + Environment.NewLine);
			addLog("Read done for " + stream.Length + " bytes!" + Environment.NewLine);
			return true;
		}

		private byte[] ReadStubMemory(int address, int length, bool rawMemory, string label)
		{
			if(length <= 0)
			{
				addErrorLine("Read length cannot be zero!");
				return null;
			}
			if(!SetBaud(baudrate))
				return null;

			int received = 0;
			void Xm_PacketReceived(XMODEM sender, byte[] packet, bool endOfFileDetected)
			{
				received += packet.Length;
				logger.setProgress(Math.Min(received, length), length);
			}

			try
			{
				logger.setProgress(0, length);
				logger.setState("Reading " + label + "...", Color.Transparent);
				var msg = new List<byte>();
				msg.AddRange(BitConverter.GetBytes(address));
				msg.AddRange(BitConverter.GetBytes(length));
				byte command = rawMemory ? CMD_STUB_XMODEM_READ_RAW
					: bUseCompressionIfPossible ? CMD_STUB_XMODEM_READ_COMPRESSED : CMD_STUB_XMODEM_READ;
				if(!rawMemory && bUseCompressionIfPossible)
					msg.Add(2);

				if(ExecuteStubCommand(command, msg.ToArray(), 2) == null)
					return null;
				using var stream = new MemoryStream();
				xm.PacketReceived += Xm_PacketReceived;
				Stopwatch sw = Stopwatch.StartNew();
				try
				{
					var result = xm.Receive(stream);
					if(result != XMODEM.TerminationReasonEnum.EndOfFile)
					{
						addErrorLine($"Read failed with {result}");
						return null;
					}
				}
				finally
				{
					sw.Stop();
					xm.PacketReceived -= Xm_PacketReceived;
				}
				logger.addLog(Environment.NewLine + $"Flash read took {sw.ElapsedMilliseconds} ms" + Environment.NewLine, Color.Gray);

				byte[] ret = stream.ToArray();
				if(!rawMemory && bUseCompressionIfPossible)
				{
					int compressedLength = ret.Length;
					ret = Decompress(ret);
					addLogLine($"Uncompressed {compressedLength} bytes to {ret.Length} bytes, compression rate - {((double)ret.Length - compressedLength) / ret.Length * 100.0:F2}%");
				}
				if(ret.Length < length)
				{
					addErrorLine($"Read {ret.Length} bytes, but expected {length}.");
					return null;
				}
				if(ret.Length != length)
					Array.Resize(ref ret, length);
				if(!rawMemory && !CheckStubCrc(address, ret))
				{
					if(!bIgnoreCRCErr)
						return null;
				}
				logger.setProgress(length, length);
				logger.setState(label + " read success!", Color.Green);
				addLogLine("Read complete!");
				return ret;
			}
			finally
			{
				if(!isCancelled) SetBaud(115200, true);
			}
		}

		private bool CheckStubCrc(int address, byte[] data)
		{
			var msg = new List<byte>();
			msg.AddRange(BitConverter.GetBytes(address));
			msg.AddRange(BitConverter.GetBytes(data.Length));
			byte[] expected = ExecuteStubCommand(CMD_STUB_CRC32, msg.ToArray(), 30, 4);
			if(expected == null)
				return false;
			uint expectedCrc = BitConverter.ToUInt32(expected, 0);
			uint actualCrc = CRC.crc32_ver2(0xFFFFFFFF, data);
			if(actualCrc != expectedCrc)
			{
				addErrorLine($"CRC32 mismatch!\r\ndevice:\t{expectedCrc:X8}\r\nflasher:\t{actualCrc:X8}");
				logger.setState("CRC32 mismatch!", Color.Red);
				return false;
			}
			addSuccess($"CRC32 matches {expectedCrc:X8}!" + Environment.NewLine);
			return true;
		}

		private bool WriteStubFlash(int address, byte[] data, bool allowUnalignedStart = false)
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

			if(!SetBaud(baudrate))
				return false;
			xm.PacketSent += Xm_PacketSent;
			try
			{
				logger.setProgress(0, alignedData.Length);
				logger.setState("Writing", Color.White);
				var msg = new List<byte>();
				msg.AddRange(BitConverter.GetBytes(alignedAddress));
				msg.AddRange(BitConverter.GetBytes(alignedData.Length));
				byte command = bUseCompressionIfPossible ? CMD_STUB_XMODEM_WRITE_COMPRESSED : CMD_STUB_XMODEM_WRITE;
				if(ExecuteStubCommand(command, msg.ToArray(), 2) == null)
					return false;

				byte[] transferData = bUseCompressionIfPossible ? Compress(alignedData) : alignedData;
				if(bUseCompressionIfPossible)
					addLogLine($"Using compression, writing {transferData.Length} bytes, compression rate - {((double)alignedData.Length - transferData.Length) / alignedData.Length * 100.0:F2}%");
				Stopwatch sw = Stopwatch.StartNew();
				int sent = xm.Send(transferData, (uint)alignedAddress);
				sw.Stop();
				logger.addLog(Environment.NewLine + $"Flash write took {sw.ElapsedMilliseconds} ms" + Environment.NewLine, Color.Gray);
				if(sent != transferData.Length)
				{
					addErrorLine($"Write failed ({xm.TerminationReason})! Expected sent bytes: {transferData.Length}, really sent: {sent}");
					return false;
				}
				if(!CheckStubCrc(alignedAddress, alignedData))
					return false;
				logger.setProgress(alignedData.Length, alignedData.Length);
				logger.setState("Writing done", Color.DarkGreen);
				addLogLine("Flash write complete.");
				return true;
			}
			finally
			{
				xm.PacketSent -= Xm_PacketSent;
			}
		}

		MemoryStream ReadInternal(int startSector, int sectors)
		{
			MemoryStream tempResult = new MemoryStream();
			if(!ReadFlash(tempResult, startSector, sectors * BK7231Flasher.SECTOR_SIZE))
			{
				logger.setState("Reading error!", Color.Red);
				SetBaud(115200);
				return null;
			}
			return tempResult;
		}

		public override void doRead(int startSector = 0x000, int sectors = 10, bool fullRead = false)
		{
			if(chipType == BKType.W600)
			{
				addErrorLine("W600 doesn't support read. Use JLink for firmware backup.");
				return;
			}
			if(doGenericSetup() == false)
			{
				return;
			}
			if(InitialiseTarget())
			{
				try
				{
					SetBaud(baudrate);
					if(fullRead)
					{
						sectors = flashSizeMB * 0x100000 / BK7231Flasher.SECTOR_SIZE;
					}
					int readAddress = chipType == BKType.W800 ? startSector : startSector | 0x08000000;
					ms = ReadInternal(readAddress, sectors);
					if(ms == null)
					{
						return;
					}
				}
				catch(Exception ex)
				{
					addErrorLine(ex.Message);
				}
				finally
				{
					if(!isCancelled) SetBaud(115200, true);
				}
			}
			return;
		}
		
		public override byte[] getReadResult()
		{
			return ms?.ToArray();
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
				if(doGenericSetup() == false || InitialiseTarget() == false)
				{
					return null;
				}
				return ReadStubMemory(target.Address.Value, target.Length.Value, true, "mask ROM");
			}
			catch(Exception ex)
			{
				addErrorLine("ROM read failed: " + ex.Message);
				return null;
			}
			finally
			{
				try
				{
					if(serial != null && serial.IsOpen) SetBaud(115200, true);
				}
				catch { }
				try { closePort(); } catch { }
			}
		}

		public override bool doErase(int startSector = 0x000, int sectors = 10, bool bAll = false)
		{
			if(!bAll)
			{
				if(chipType == BKType.W600)
				{
					addErrorLine("W600 range erase is not implemented.");
					return false;
				}
				if(startSector < 0x2000 || (startSector & (BK7231Flasher.SECTOR_SIZE - 1)) != 0 || sectors <= 0)
				{
					addErrorLine("W800 erase range must be 0x1000-aligned, in writable flash, and contain at least one sector.");
					return false;
				}
			}

			if(doGenericSetup() == false)
			{
				return false;
			}
			if(InitialiseTarget() == false)
			{
				return false;
			}

			bool ok;
			if(chipType == BKType.W600)
			{
				ok = EraseAndWait("Erasing W600 flash...", null, 24);
			}
			else if(bAll)
			{
				addLogLine("Erasing writable W800 flash (the protected first 8 KiB is preserved)...");
				ok = ExecuteStubCommand(CMD_STUB_FLASH_CHIP_ERASE, timeout: 180) != null;
			}
			else
			{
				int length = sectors * BK7231Flasher.SECTOR_SIZE;
				var msg = new List<byte>();
				msg.AddRange(BitConverter.GetBytes(startSector));
				msg.AddRange(BitConverter.GetBytes(length));
				addLogLine($"Erasing W800 flash at 0x{startSector:X}, length 0x{length:X}...");
				ok = ExecuteStubCommand(CMD_STUB_FLASH_ERASE, msg.ToArray(), 60) != null;
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
		
		public override void closePort()
		{
			if(serial != null)
			{
				serial.Close();
				serial.Dispose();
			}
		}

		public override void doReadAndWrite(int startSector, int sectors, string sourceFileName, WriteMode rwMode)
		{
			if(chipType == BKType.W600)
			{
				if(rwMode == WriteMode.ReadAndWrite)
				{
					addErrorLine("W600 doesn't support read. Use JLink for firmware backup.");
					return;
				}
				else if(rwMode == WriteMode.OnlyOBKConfig)
				{
					addErrorLine("Writing only OBK config is disabled for W600, use \"Automatically configure OBK on flash write\".");
					return;
				}
			}
			if(doGenericSetup() == false)
			{
				return;
			}
			if(InitialiseTarget())
			{
				try
				{
					if(chipType != BKType.W800)
						xm.PacketSent += Xm_PacketSent;
					SetBaud(baudrate);
					OBKConfig cfg = rwMode == WriteMode.OnlyOBKConfig ? logger.getConfig() : logger.getConfigToWrite();
					if(rwMode == WriteMode.ReadAndWrite)
					{
						sectors = flashSizeMB * 0x100000 / BK7231Flasher.SECTOR_SIZE;
						addLogLine($"Flash size detected: {sectors / 256}MB");
						int readAddress = chipType == BKType.W800 ? startSector : startSector | 0x08000000;
						ms = ReadInternal(readAddress, sectors);
						if(ms == null)
						{
							return;
						}
						if(saveReadResult(startSector) == false)
						{
							return;
						}
					}
					if(rwMode == WriteMode.OnlyWrite || rwMode == WriteMode.ReadAndWrite && !isCancelled)
					{
						if(string.IsNullOrEmpty(sourceFileName))
						{
							addLogLine("No filename given!");
							return;
						}
						addLogLine("Reading " + sourceFileName + "...");
						byte[] data = File.ReadAllBytes(sourceFileName);
						addLogLine("Starting flash write " + data.Length);
						logger.setState("Writing", Color.White);
						if(chipType == BKType.W800)
						{
							bool writeOk;
							if(bCustomWriteMode)
							{
								writeOk = WriteStubFlash(startSector, data);
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
								writeOk = WriteStubFlash(startSector, cutData);
							}
							else
							{
								addErrorLine("Unknown file type, skipping.");
								return;
							}
							if(!writeOk)
								return;
						}
						else if(sourceFileName.EndsWith(".fls", StringComparison.OrdinalIgnoreCase))
						{
							var res = xm.Send(data);
							if(res == data.Length)
							{
								logger.setState("Writing done", Color.DarkGreen);
								addLogLine("Done flash write " + data.Length);
							}
							else
							{
								logger.setState("Write error!", Color.Red);
								addErrorLine("Write error!");
							}
						}
						else if(data.Length >= 0x100000)
						{
							try
							{
								startSector = 0x2000;
								var secBootHeader = new byte[64];
								Array.Copy(data, 0x2000, secBootHeader, 0, secBootHeader.Length);
								if(secBootHeader[0] != 0x9f || secBootHeader[1] != 0xff || secBootHeader[2] != 0xff || secBootHeader[3] != 0xa0)
								{
									addErrorLine("Unknown file type, no firmware header at 0x2000!");
									return;
								}
								var cutData = new byte[data.Length - startSector];
								Array.Copy(data, startSector, cutData, 0, cutData.Length);
								startSector |= 0x08000000;
								if(secBootHeader[60] != 0xFF || secBootHeader[61] != 0xFF || secBootHeader[62] != 0xFF || secBootHeader[63] != 0xFF)
								{
									addErrorLine("Not W600 backup!");
									return;
								}
								var fls = GenerateW600PseudoFLSFromData(cutData, startSector);
								var res = xm.Send(fls, (uint)(startSector ^ 0x08000000));
								if(res == fls.Length)
								{
									logger.setState("Writing done", Color.DarkGreen);
									addLogLine("Done flash write " + data.Length);
									logger.setProgress(1, 1);
								}
								else
								{
									logger.setState("Write error!", Color.Red);
								}
							}
							catch(Exception ex)
							{
								addErrorLine(ex.Message);
								return;
							}
						}
						else
						{
							addErrorLine("Unknown file type, skipping.");
							return;
						}
					}
					if((rwMode == WriteMode.OnlyWrite || rwMode == WriteMode.ReadAndWrite || rwMode == WriteMode.OnlyOBKConfig) && cfg != null && !isCancelled)
					{
						var offset = (OBKFlashLayout.getConfigLocation(chipType, out _) | 0x08000000);
						cfg.saveConfig(chipType);
						var data = new byte[2016 + 0x303];
						if(chipType == BKType.W800)
						{
							MiscUtils.padArray(data, 1);
							Array.Copy(cfg.getData(), 0, data, 0x303, 2016);
						}
						else
						{
							data = new byte[2016];
							Array.Copy(cfg.getData(), data, data.Length);
						}
						addLog("Now will also write OBK config..." + Environment.NewLine);
						addLog("Long name from CFG: " + cfg.longDeviceName + Environment.NewLine);
						addLog("Short name from CFG: " + cfg.shortDeviceName + Environment.NewLine);
						addLog("Web Root from CFG: " + cfg.webappRoot + Environment.NewLine);
						bool configWritten;
						if(chipType == BKType.W800)
						{
							configWritten = WriteStubFlash((offset ^ 0x08000000) - 0x303, data);
						}
						else
						{
							var fls = GenerateW600PseudoFLSFromData(data, offset);
							configWritten = xm.Send(fls, (uint)(offset ^ 0x08000000)) == fls.Length;
						}
						if(configWritten)
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
					if(chipType != BKType.W800)
						xm.PacketSent -= Xm_PacketSent;
					if(!isCancelled) SetBaud(115200, true);
				}
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
				if(!WriteStubFlash((int)(headerAddress - flashBase), image, true))
					return false;
				cursor += headerLength + (int)length;
				segment++;
			}
			return segment > 0;
		}

		byte[] GenerateW600PseudoFLSFromData(byte[] data, int startAddr)
		{
			var crc = CRC.crc32_ver2(0xFFFFFFFF, data);
			var fls = new List<byte>()
			{
				0x9F, 0xFF, 0xFF, 0xA0,
				0x00, 0x02, 0x00, 0x00,
				(byte)(startAddr & 0xFF),
				(byte)((startAddr >> 8) & 0xFF),
				(byte)((startAddr >> 16) & 0xFF),
				(byte)((startAddr >> 24) & 0xFF),
				(byte)(data.Length & 0xFF),
				(byte)((data.Length >> 8) & 0xFF),
				(byte)((data.Length >> 16) & 0xFF),
				(byte)((data.Length >> 24) & 0xFF),
				(byte)(crc & 0xFF),
				(byte)((crc >> 8) & 0xFF),
				(byte)((crc >> 16) & 0xFF),
				(byte)((crc >> 24) & 0xFF),
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x31, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,
			};
			var crcHdr = CRC.crc32_ver2(0xFFFFFFFF, fls.ToArray());
			fls.Add((byte)(crcHdr & 0xFF));
			fls.Add((byte)((crcHdr >> 8) & 0xFF));
			fls.Add((byte)((crcHdr >> 16) & 0xFF));
			fls.Add((byte)((crcHdr >> 24) & 0xFF));
			fls.AddRange(data);
			return fls.ToArray();
		}

		bool saveReadResult(string fileName)
		{
			if(ms == null)
			{
				addError("There was no result to save." + Environment.NewLine);
				return false;
			}
			byte[] dat = ms.ToArray();
			string fullPath = "backups/" + fileName;
			File.WriteAllBytes(fullPath, dat);
			addSuccess("Wrote " + dat.Length + " to " + fileName + Environment.NewLine);
			logger.onReadResultQIOSaved(dat, "", fullPath);
			return true;
		}
		public override bool saveReadResult(int startOffset)
		{
			string fileName = MiscUtils.formatDateNowFileName("readResult_" + chipType, backupName, "bin");
			return saveReadResult(fileName);
		}

		internal override byte[] ReadMAC()
		{
			if(chipType != BKType.W800 || serial == null || !serial.IsOpen)
				return null;
			return ExecuteStubCommand(CMD_STUB_GET_MAC, expectedReplyLen: 6, isErrorExpected: true);
		}
	}
}

