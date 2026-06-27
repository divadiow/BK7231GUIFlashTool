using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace BK7231Flasher
{
	static class EasyFlash
	{
		internal static int EF_WRITE_GRAN = 1;

		internal static bool RequireKVHeader = false;

		internal static bool AlternateSectorHeader = false;

		private const int ENV_UNUSED = 0;
		private const int ENV_PRE_WRITE = 1;
		private const int ENV_WRITE = 2;
		private const int ENV_PRE_DELETE = 3;
		private const int ENV_DELETED = 4;
		private const int ENV_ERR_HDR = 5;
		private const int ENV_STATUS_NUM = 6;

		private const int SECTOR_STORE_UNUSED = 0;
		private const int SECTOR_STORE_EMPTY = 1;
		private const int SECTOR_STORE_USING = 2;
		private const int SECTOR_STORE_FULL = 3;
		private const int SECTOR_STORE_STATUS_NUM = 4;

		private static int WriteGran => EF_WRITE_GRAN < 8 ? 1 : EF_WRITE_GRAN / 8;

		private static int SECTOR_HDR => EF_WRITE_GRAN == 32 ? 0x24 : EF_WRITE_GRAN == 8 ? 0x14 : 0x10;

		private static int SectorMagicOffset => EF_WRITE_GRAN == 32 ? 0x18 : EF_WRITE_GRAN == 8 ? 0x08 : 0x04;

		//private static int DataOff => EF_WRITE_GRAN == 32 ? 0x14 : EF_WRITE_GRAN == 8 ? 12 : 8;
		private static int DataOff => EF_WRITE_GRAN == 32 ? 28 - (RequireKVHeader ? 0 : 8) : EF_WRITE_GRAN == 8 ? 12 - (RequireKVHeader ? 0 : 4) : (RequireKVHeader ? 8 : 4);

		private static byte ExpectedSectorMagicLastByte => AlternateSectorHeader ? (byte)'1' : (byte)'0';

		private static void ConfigureLayout(BKType type)
		{
			RequireKVHeader = true;
			AlternateSectorHeader = false;

			switch(type)
			{
				case BKType.ECR6600:
					EF_WRITE_GRAN = 32;
					RequireKVHeader = false;
					AlternateSectorHeader = true;
					break;
				case BKType.TR6260:
					EF_WRITE_GRAN = 32;
					RequireKVHeader = false;
					break;
				case BKType.BL602:
				case BKType.BL616:
				case BKType.BL702:
					EF_WRITE_GRAN = 8;
					break;
				default:
					EF_WRITE_GRAN = 1;
					break;
			}
		}

		public static byte[] LoadValueFromData(byte[] data, string sname, int size, BKType type, out byte[] efdata)
		{
			efdata = data;
			if(data == null)
				return null;

			ConfigureLayout(type);
			return NativeEF_Load(data, sname, size);
		}

		public static byte[] SaveValueToNewEasyFlash(string sname, byte[] cfgData, int areaSize, BKType type)
		{
			ConfigureLayout(type);
			return NativeEF_Save(sname, cfgData, areaSize);
		}

		public static byte[] SaveValueToExistingEasyFlash(string sname, byte[] efData, byte[] cfgData, int areaSize, BKType type)
		{
			ConfigureLayout(type);
			// Read all existing WRITE-committed entries, replace/insert the target key, reserialize.
			// This preserves other keys that the firmware wrote (nv_version, OBK_FV,
			// StaSSID, StaPW, PMK, etc.) rather than wiping them. Deleted/stale entries
			// are deliberately not preserved.
			var existing = NativeEF_ReadAllEntries(efData, areaSize);
			existing[sname] = cfgData;
			return NativeEF_SaveEntries(existing, areaSize, sname);
		}

		// ---------------------------------------------------------------------------
		// Native EasyFlash implementation for OpenBeken flasher config areas.
		//
		// Supported OpenBeken layouts:
		//   * EF_WRITE_GRAN=1,  EF40 sectors, KV40 entries: RTL/XR/RDA style.
		//   * EF_WRITE_GRAN=8,  EF40 sectors, KV40 entries: BL602/BL616/BL702 style.
		//   * EF_WRITE_GRAN=32, EF40 sectors, no KV40 entry magic: TR6260 style.
		//   * EF_WRITE_GRAN=32, EF41 sectors, no KV40 entry magic: ECR6600 style.
		//
		// Important EasyFlash details implemented here:
		//   * CRC is calculated over name_len, padded value_len field, aligned name,
		//     and aligned value. For EF_WRITE_GRAN=32 this means value_len is padded
		//     to 4 bytes for CRC purposes even though value_len stores the real length.
		//   * Only entries with exact ENV_WRITE status are accepted. PRE_WRITE,
		//     PRE_DELETE, DELETED and ERR_HDR entries are ignored so stale/deleted keys
		//     are not resurrected during SaveValueToExistingEasyFlash().
		//   * ECR6600 uses EF41 sectors and is intentionally not treated as EF40.
		// ---------------------------------------------------------------------------

		private static int WgAlign(int x) => (x + (WriteGran - 1)) & ~(WriteGran - 1);

		private static uint Crc32Ieee(byte[] data, int offset, int length) => CRC.crc32_ver2(0xFFFFFFFF, data, length, (uint)offset) ^ 0xFFFFFFFF;

		private static void ValidateAreaSize(int areaSize)
		{
			if(areaSize <= 0 || (areaSize % BK7231Flasher.SECTOR_SIZE) != 0)
				throw new InvalidOperationException($"Invalid EasyFlash area size: {areaSize}. It must be a positive multiple of {BK7231Flasher.SECTOR_SIZE} bytes.");
		}

		private static int StatusTableSize(int statusNum)
		{
			if(EF_WRITE_GRAN == 1)
				return (statusNum * EF_WRITE_GRAN + 7) / 8;
			return ((statusNum - 1) * EF_WRITE_GRAN + 7) / 8;
		}

		private static int ReadEfStatus(byte[] data, int pos, int statusNum)
		{
			for(int statusIndex = statusNum - 1; statusIndex >= 1; statusIndex--)
			{
				if(EF_WRITE_GRAN == 1)
				{
					int bitIndex = statusIndex - 1;
					int byteIndex = pos + (bitIndex / 8);
					int bitMask = 0x80 >> (bitIndex % 8);
					if(byteIndex < data.Length && (data[byteIndex] & bitMask) == 0x00)
						return statusIndex;
				}
				else
				{
					int byteIndex = pos + ((statusIndex - 1) * WriteGran);
					if(byteIndex < data.Length && data[byteIndex] == 0x00)
						return statusIndex;
				}
			}

			return ENV_UNUSED;
		}

		private static bool SectorMagicIsExpected(byte[] data, int sectorOff)
		{
			int magic = sectorOff + SectorMagicOffset;
			return magic + 4 <= data.Length &&
				data[magic + 0] == (byte)'E' &&
				data[magic + 1] == (byte)'F' &&
				data[magic + 2] == (byte)'4' &&
				data[magic + 3] == ExpectedSectorMagicLastByte;
		}

		private static bool EntryMagicIsExpected(byte[] data, int pos)
		{
			if(!RequireKVHeader)
				return true;

			int magic = pos + SectorMagicOffset;
			return magic + 4 <= data.Length &&
				data[magic + 0] == (byte)'K' &&
				data[magic + 1] == (byte)'V' &&
				data[magic + 2] == (byte)'4' &&
				data[magic + 3] == (byte)'0';
		}

		/// <summary>
		/// Build a single EasyFlash entry (status_table + header + name + value).
		/// </summary>
		internal static byte[] NativeEF_MakeEntry(string key, byte[] value)
		{
			if(string.IsNullOrEmpty(key))
				throw new ArgumentException("EasyFlash key must not be empty.", nameof(key));
			if(value == null)
				throw new ArgumentNullException(nameof(value));

			byte[] keyBytes = Encoding.ASCII.GetBytes(key);
			int nameLen = keyBytes.Length;
			int valueLen = value.Length;

			if(nameLen <= 0 || nameLen > byte.MaxValue)
				throw new InvalidOperationException($"Invalid EasyFlash key length for '{key}': {nameLen}.");

			int nameSz = WgAlign(nameLen);
			int valSz = WgAlign(valueLen);

			int totalLen = DataOff + 16 + nameSz + valSz;
			if(totalLen > BK7231Flasher.SECTOR_SIZE - SECTOR_HDR)
				throw new InvalidOperationException($"EasyFlash entry '{key}' is too large for one sector ({totalLen} bytes, maximum {BK7231Flasher.SECTOR_SIZE - SECTOR_HDR} bytes).");

			byte[] entry = new byte[totalLen];
			entry.AsSpan().Fill(0xFF);

			if(EF_WRITE_GRAN == 1)
			{
				entry[0] = 0x3F; // ENV_PRE_WRITE && ENV_WRITE
			}
			else
			{
				entry[0] = 0x00; // ENV_PRE_WRITE
				entry[WriteGran] = 0x00; // ENV_WRITE
			}

			if(RequireKVHeader)
			{
				entry[SectorMagicOffset + 0] = (byte)'K';
				entry[SectorMagicOffset + 1] = (byte)'V';
				entry[SectorMagicOffset + 2] = (byte)'4';
				entry[SectorMagicOffset + 3] = (byte)'0';
			}

			MiscUtils.WriteU32LE(entry, DataOff, (uint)totalLen);

			int crcBufSize = 8 + nameSz + valSz;
			byte[] crcBuf = new byte[crcBufSize];
			crcBuf.AsSpan().Fill(0xFF);
			crcBuf[0] = (byte)nameLen;

			crcBuf[4] = (byte)(valueLen);
			crcBuf[5] = (byte)(valueLen >> 8);
			crcBuf[6] = (byte)(valueLen >> 16);
			crcBuf[7] = (byte)(valueLen >> 24);

			Array.Copy(keyBytes, 0, crcBuf, 8, nameLen);
			Array.Copy(value, 0, crcBuf, 8 + nameSz, valueLen);

			uint crc32 = Crc32Ieee(crcBuf, 0, crcBuf.Length);
			MiscUtils.WriteU32LE(entry, DataOff + 4, crc32);
			entry[DataOff + 8] = (byte)nameLen;
			MiscUtils.WriteU32LE(entry, DataOff + 12, (uint)valueLen);

			int dataStart = DataOff + 16;
			Array.Copy(keyBytes, 0, entry, dataStart, nameLen);
			Array.Copy(value, 0, entry, dataStart + nameSz, valueLen);

			return entry;
		}

		/// <summary>
		/// Build an EF sector header.
		/// isActive=true  → STORE_USING (has entries); false → STORE_EMPTY (blank formatted).
		/// </summary>
		internal static byte[] NativeEF_MakeSectorHdr(bool isActive)
		{
			byte[] h = new byte[SECTOR_HDR];
			h.AsSpan().Fill(0xFF);
			if(EF_WRITE_GRAN == 1)
			{
				if(isActive)
				{
					h[0] = 0x3F; // EMPTY && USING
					h[1] = 0x7F; // DIRTY_FALSE
				}
				else
				{
					h[0] = 0x7F; // EMPTY
					h[1] = 0x7F; // DIRTY_FALSE
				}
			}
			else
			{
				int storeTableSize = StatusTableSize(SECTOR_STORE_STATUS_NUM);
				h[0] = 0x00;  // store word[0] = EMPTY written
				if(isActive)
					h[WriteGran] = 0x00;  // store word[1] = USING written
				h[storeTableSize + 0] = 0x00;  // dirty word[0] = DIRTY_FALSE written
			}
			h[SectorMagicOffset + 0] = (byte)'E';
			h[SectorMagicOffset + 1] = (byte)'F';
			h[SectorMagicOffset + 2] = (byte)'4';
			h[SectorMagicOffset + 3] = ExpectedSectorMagicLastByte;
			return h;
		}

		private static List<KeyValuePair<string, byte[]>> OrderEntries(Dictionary<string, byte[]> kvPairs, string preferredFirstKey)
		{
			var entries = kvPairs.Select(kv => new KeyValuePair<string, byte[]>(kv.Key, kv.Value)).ToList();

			if(!string.IsNullOrEmpty(preferredFirstKey))
			{
				entries.Sort((a, b) =>
				{
					bool aPreferred = string.Equals(a.Key, preferredFirstKey, StringComparison.Ordinal);
					bool bPreferred = string.Equals(b.Key, preferredFirstKey, StringComparison.Ordinal);
					if(aPreferred && !bPreferred) return -1;
					if(!aPreferred && bPreferred) return 1;
					return string.CompareOrdinal(a.Key, b.Key);
				});
			}
			else
			{
				entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
			}

			return entries;
		}

		/// <summary>
		/// Serialize key/value pairs into a complete EasyFlash area image.
		/// Entries are packed across as many sectors as needed; remaining sectors
		/// are written as blank-but-formatted (magic present, no entries).
		/// </summary>
		internal static byte[] NativeEF_SaveEntries(Dictionary<string, byte[]> kvPairs, int areaSize, string preferredFirstKey = null)
		{
			ValidateAreaSize(areaSize);
			if(kvPairs == null)
				throw new ArgumentNullException(nameof(kvPairs));

			int SECTOR_AVAIL = BK7231Flasher.SECTOR_SIZE - SECTOR_HDR;
			int nSectors = areaSize / BK7231Flasher.SECTOR_SIZE;

			// Pack entries into sectors, splitting when a sector would overflow.
			var sectors = new List<List<KeyValuePair<string, byte[]>>>();
			var cur = new List<KeyValuePair<string, byte[]>>();
			int curUsed = 0;

			foreach(var kv in OrderEntries(kvPairs, preferredFirstKey))
			{
				byte[] entry = NativeEF_MakeEntry(kv.Key, kv.Value);
				if(entry.Length > SECTOR_AVAIL)
					throw new InvalidOperationException($"EasyFlash entry '{kv.Key}' is too large for one sector ({entry.Length} bytes, maximum {SECTOR_AVAIL} bytes).");

				if(curUsed + entry.Length > SECTOR_AVAIL)
				{
					if(cur.Count == 0)
						throw new InvalidOperationException($"EasyFlash entry '{kv.Key}' does not fit in an empty sector.");
					sectors.Add(cur);
					cur = new List<KeyValuePair<string, byte[]>>();
					curUsed = 0;
				}

				cur.Add(kv);
				curUsed += entry.Length;
			}
			if(cur.Count > 0)
				sectors.Add(cur);

			if(sectors.Count > nSectors)
				throw new InvalidOperationException($"EasyFlash data does not fit in configured area: needs {sectors.Count} sectors, area has {nSectors} sectors.");

			byte[] result = new byte[areaSize];
			result.AsSpan().Fill(0xFF);

			byte[] blankHdr = NativeEF_MakeSectorHdr(false);

			for(int s = 0; s < nSectors; s++)
			{
				int base_ = s * BK7231Flasher.SECTOR_SIZE;
				if(s < sectors.Count)
				{
					byte[] hdr = NativeEF_MakeSectorHdr(true);
					Array.Copy(hdr, 0, result, base_, SECTOR_HDR);
					int pos = base_ + SECTOR_HDR;
					foreach(var kv in sectors[s])
					{
						byte[] entry = NativeEF_MakeEntry(kv.Key, kv.Value);
						if(pos + entry.Length > base_ + BK7231Flasher.SECTOR_SIZE)
							throw new InvalidOperationException($"Internal EasyFlash packing error for key '{kv.Key}'.");
						Array.Copy(entry, 0, result, pos, entry.Length);
						pos += entry.Length;
					}
				}
				else
				{
					Array.Copy(blankHdr, 0, result, base_, SECTOR_HDR);
				}
			}

			return result;
		}

		/// <summary>
		/// Build a fresh EasyFlash area image containing a single config key/value entry.
		/// Used when no existing EF data is available (SaveValueToNewEasyFlash path).
		/// </summary>
		internal static byte[] NativeEF_Save(string key, byte[] value, int areaSize)
		{
			var kv = new Dictionary<string, byte[]> { { key, value }, { "nv_version", new byte[] { 0x30, 0x2e, 0x30, 0x31 } } };
			return NativeEF_SaveEntries(kv, areaSize, key);
		}

		/// <summary>
		/// Read all exact ENV_WRITE entries from an EasyFlash area.
		/// Returns a dictionary of key -> value (latest value wins for duplicate keys).
		/// </summary>
		internal static Dictionary<string, byte[]> NativeEF_ReadAllEntries(byte[] data, int size = -1)
		{
			var result = new Dictionary<string, byte[]>();
			if(data == null)
				return result;

			int scanSize = size > 0 ? Math.Min(size, data.Length) : data.Length;
			scanSize -= scanSize % BK7231Flasher.SECTOR_SIZE;
			if(scanSize <= 0)
				return result;

			for(int sectorOff = 0; sectorOff + BK7231Flasher.SECTOR_SIZE <= scanSize; sectorOff += BK7231Flasher.SECTOR_SIZE)
			{
				if(!SectorMagicIsExpected(data, sectorOff))
					continue;

				int sectorStatus = ReadEfStatus(data, sectorOff, SECTOR_STORE_STATUS_NUM);
				if(sectorStatus != SECTOR_STORE_USING && sectorStatus != SECTOR_STORE_FULL)
					continue;

				int pos = sectorOff + SECTOR_HDR;
				int sectorEnd = sectorOff + BK7231Flasher.SECTOR_SIZE;
				while(pos + DataOff + 16 <= sectorEnd)
				{
					int envStatus = ReadEfStatus(data, pos, ENV_STATUS_NUM);
					if(envStatus == ENV_UNUSED)
						break;

					int off = DataOff;
					if(pos + off + 16 > sectorEnd)
						break;

					int totalLen = (int)MiscUtils.ReadU32LE(data, pos + off);
					if(totalLen == unchecked((int)0xFFFFFFFF) || totalLen <= 0 || totalLen < off + 16 ||
					   pos + totalLen > sectorEnd)
						break;

					if(!EntryMagicIsExpected(data, pos))
					{
						pos += totalLen;
						continue;
					}

					if(envStatus != ENV_WRITE)
					{
						pos += totalLen;
						continue;
					}

					int nameLen  = data[pos + off + 8];
					int valueLen = (int)MiscUtils.ReadU32LE(data, pos + off + 12);
					if(nameLen <= 0 || valueLen < 0)
					{
						pos += totalLen;
						continue;
					}

					int nameSz = WgAlign(nameLen);
					int valSz = WgAlign(valueLen);
					int payloadLen = 16 + nameSz + valSz;
					if(off + payloadLen > totalLen || pos + off + payloadLen > sectorEnd)
					{
						pos += totalLen;
						continue;
					}

					uint crcRead = MiscUtils.ReadU32LE(data, pos + off + 4);
					uint crcEf = Crc32Ieee(data, pos + off + 8, 8 + nameSz + valSz);
					if(crcRead != crcEf)
					{
						Console.WriteLine("Bad CRC32 for EF entry! Skipping...");
						pos += totalLen;
						continue;
					}

					int nameStart = pos + off + 16;
					int valStart = nameStart + nameSz;
					if(nameStart + nameLen <= sectorEnd && valStart + valueLen <= sectorEnd)
					{
						string name = Encoding.ASCII.GetString(data, nameStart, nameLen);
						byte[] val = new byte[valueLen];
						Array.Copy(data, valStart, val, 0, valueLen);
						result[name] = val;  // latest exact ENV_WRITE wins
					}

					pos += totalLen;
				}
			}

			return result;
		}

		/// <summary>
		/// Scan an EasyFlash area image and return the most recent value
		/// stored under 'key', or null if not found.
		/// </summary>
		internal static byte[] NativeEF_Load(byte[] data, string key, int size = -1)
		{
			var all = NativeEF_ReadAllEntries(data, size);
			return all.TryGetValue(key, out var val) ? val : null;
		}
	}
}
