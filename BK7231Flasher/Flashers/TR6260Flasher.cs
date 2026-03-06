using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace BK7231Flasher
{
    // TR6260 implementation based on reverse-engineered UTPmain/trstool protocol.
    public class TR6260Flasher : BaseFlasher
    {
        const int DEFAULT_BAUD = 57600;
        const int BLOCK_SIZE = 4096;
        const int PARTITION_ADDR = 0x6000;
        const int APP_ADDR = 0x7000;

        // trstool protocol constants
        const byte SOF = 0xA5;
        const byte M_SYNC = 0;
        const byte M_CFG = 1;
        const byte M_WRITE = 2;
        const byte M_READ = 3;
        const byte M_ERASE = 4;

        const byte M_SUB_SYNC = 0;
        const byte M_SUB_BAUD = 1;
        const byte M_SUB_FLASH = 2;

        public TR6260Flasher(CancellationToken ct) : base(ct) { }

        bool SetupPort(int initialBaud = DEFAULT_BAUD)
        {
            try
            {
                serial = new SerialPort(serialName, initialBaud)
                {
                    ReadTimeout = 1500,
                    WriteTimeout = 1500
                };
                serial.Open();
                serial.DiscardInBuffer();
                serial.DiscardOutBuffer();
                return true;
            }
            catch(Exception ex)
            {
                addErrorLine("TR6260: failed to open port: " + ex.Message);
                return false;
            }
        }

        uint Crc32(byte[] data, int len)
        {
            return CRC.crc32_ver2(0xFFFFFFFF, data, len) ^ 0xFFFFFFFF;
        }

        byte[] BuildPacket(byte cmd, byte sub, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var headerRaw = new byte[1 + 1 + 4];
            headerRaw[0] = cmd;
            headerRaw[1] = sub;
            Array.Copy(BitConverter.GetBytes(payload.Length), 0, headerRaw, 2, 4);
            var headerCrc = Crc32(headerRaw, headerRaw.Length);

            var outp = new List<byte>(1 + 1 + 1 + 4 + 4 + payload.Length + 4);
            outp.Add(SOF);
            outp.Add(cmd);
            outp.Add(sub);
            outp.AddRange(BitConverter.GetBytes(payload.Length));
            outp.AddRange(BitConverter.GetBytes(headerCrc));
            outp.AddRange(payload);
            outp.AddRange(BitConverter.GetBytes(Crc32(payload, payload.Length)));
            return outp.ToArray();
        }

        bool SendCommand(byte cmd, byte sub, byte[] payload, out byte[] response)
        {
            response = null;
            try
            {
                var pkt = BuildPacket(cmd, sub, payload);
                serial.DiscardInBuffer();
                serial.Write(pkt, 0, pkt.Length);
                Thread.Sleep(30);

                if(serial.BytesToRead <= 0)
                    return false;

                var buf = new byte[serial.BytesToRead];
                serial.Read(buf, 0, buf.Length);
                response = buf;
                return true;
            }
            catch
            {
                return false;
            }
        }

        bool SyncAndConfigure()
        {
            addLogLine("TR6260: syncing bootrom...");
            for(int i = 0; i < 10; i++)
            {
                if(SendCommand(M_SYNC, M_SUB_SYNC, new byte[] { 0 }, out var _))
                {
                    addLogLine("TR6260: sync OK");
                    break;
                }
                Thread.Sleep(120);
                if(i == 9)
                {
                    addErrorLine("TR6260: sync failed");
                    return false;
                }
            }

            if(baudrate > 0 && baudrate != DEFAULT_BAUD)
            {
                var p = BitConverter.GetBytes(baudrate);
                if(!SendCommand(M_CFG, M_SUB_BAUD, p, out var _))
                {
                    addWarningLine("TR6260: baud change command failed, keeping default baud");
                }
                else
                {
                    Thread.Sleep(30);
                    serial.BaudRate = baudrate;
                }
            }
            return true;
        }

        bool FlashBegin(int offset, int len)
        {
            var p = new byte[8];
            Array.Copy(BitConverter.GetBytes(offset), 0, p, 0, 4);
            Array.Copy(BitConverter.GetBytes(len), 0, p, 4, 4);
            return SendCommand(M_WRITE, M_SUB_FLASH, p, out var _);
        }

        bool ReadBegin(int offset, int len)
        {
            var p = new byte[8];
            Array.Copy(BitConverter.GetBytes(offset), 0, p, 0, 4);
            Array.Copy(BitConverter.GetBytes(len), 0, p, 4, 4);
            return SendCommand(M_READ, M_SUB_FLASH, p, out var _);
        }


        byte[] LoadTr6260Asset(string assetName, string fileName)
        {
            try
            {
                var embedded = FLoaders.GetBinaryFromAssembly(assetName);
                if(embedded != null && embedded.Length > 0)
                    return embedded;
            }
            catch
            {
                // optional embedded resource may be missing in source-only PRs
            }

            string[] candidates = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Floaders", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
                Path.Combine("BK7231Flasher", "Floaders", fileName),
                Path.Combine("Floaders", fileName),
            };

            foreach(var path in candidates.Distinct())
            {
                if(File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            return null;
        }

        public override void doWrite(int startSector, byte[] data)
        {
            if(!SetupPort()) return;
            if(!SyncAndConfigure()) return;

            // For full-write workflows include partition table at 0x6000 like UTP tool.
            if(startSector == 0)
            {
                var boot = LoadTr6260Asset("TR6260_Boot", "TR6260_Boot.bin");
                if(boot != null && boot.Length > 0)
                {
                    addLogLine("TR6260: writing bundled bootloader...");
                    if(FlashBegin(0, boot.Length))
                        WriteDataBlocks(boot);
                }
                else
                {
                    addWarningLine("TR6260: TR6260_Boot.bin not found (embedded or disk), skipping bootloader stage.");
                }

                var partition = LoadTr6260Asset("TR6260_Partition", "TR6260_Partition.bin");
                if(partition != null && partition.Length > 0)
                {
                    addLogLine("TR6260: writing bundled partition table...");
                    if(FlashBegin(PARTITION_ADDR, partition.Length))
                        WriteDataBlocks(partition);
                }
                else
                {
                    addWarningLine("TR6260: TR6260_Partition.bin not found (embedded or disk), skipping partition stage.");
                }
            }

            int address = startSector * BK7231Flasher.SECTOR_SIZE;
            if(address == 0)
                address = APP_ADDR;

            addLogLine($"TR6260: writing {data.Length} bytes at 0x{address:X}");
            if(!FlashBegin(address, data.Length))
            {
                addErrorLine("TR6260: flash begin failed");
                return;
            }
            WriteDataBlocks(data);
        }

        void WriteDataBlocks(byte[] data)
        {
            int done = 0;
            int seq = 0;
            while(done < data.Length)
            {
                int take = Math.Min(BLOCK_SIZE, data.Length - done);
                var block = new byte[take + 4];
                Array.Copy(BitConverter.GetBytes(seq), 0, block, 0, 4);
                Array.Copy(data, done, block, 4, take);
                if(!SendCommand(M_WRITE, M_SUB_FLASH, block, out var _))
                {
                    addErrorLine($"TR6260: write failed at {done}");
                    return;
                }
                done += take;
                seq++;
                logger.setProgress(done, data.Length);
            }
            addSuccess("TR6260 write completed.\n");
        }

        public override void doRead(int startSector = 0, int sectors = 10, bool fullRead = false)
        {
            if(!SetupPort()) return;
            if(!SyncAndConfigure()) return;

            int offset = startSector * BK7231Flasher.SECTOR_SIZE;
            int len = sectors * BK7231Flasher.SECTOR_SIZE;
            if(fullRead)
            {
                offset = 0;
                len = 0x100000; // TR6260_1M
            }
            if(!ReadBegin(offset, len))
            {
                addErrorLine("TR6260: read begin failed");
                return;
            }

            ms = new MemoryStream();
            int remaining = len;
            while(remaining > 0)
            {
                int ask = Math.Min(BLOCK_SIZE, remaining);
                try
                {
                    var buf = new byte[ask];
                    int got = serial.Read(buf, 0, ask);
                    if(got <= 0) break;
                    ms.Write(buf, 0, got);
                    remaining -= got;
                    logger.setProgress(len - remaining, len);
                }
                catch { break; }
            }
            addSuccess("TR6260 read completed.\n");
        }

        MemoryStream ms;
        public override byte[] getReadResult() => ms?.ToArray();

        public override bool doErase(int startSector = 0, int sectors = 10, bool bAll = false)
        {
            if(!SetupPort()) return false;
            if(!SyncAndConfigure()) return false;

            int offset = bAll ? 0 : (startSector * BK7231Flasher.SECTOR_SIZE);
            int len = bAll ? 0x100000 : (sectors * BK7231Flasher.SECTOR_SIZE);
            var p = new byte[8];
            Array.Copy(BitConverter.GetBytes(offset), 0, p, 0, 4);
            Array.Copy(BitConverter.GetBytes(len), 0, p, 4, 4);
            if(!SendCommand(M_ERASE, M_SUB_FLASH, p, out var _))
            {
                addErrorLine("TR6260: erase failed");
                return false;
            }
            addSuccess("TR6260 erase completed.\n");
            return true;
        }
    }
}
