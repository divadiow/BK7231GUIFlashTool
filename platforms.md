| Platform | Read | Write | Erase | OBK config write | RF restore | Custom R/W | RF relocation | Tuya config extraction |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| BK7231T | ✅ | ✅³ | ✅⁷ | ✅ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7231U | ✅ | ✅³ | ✅⁷ | ✅ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7231N (T2, T34) | ✅ | ✅ | ✅⁷ | ✅ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7231M | ✅ | ✅ | ✅⁷ | ✅ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7236 (T3) | ✅ | ✅ | ⚠️²⁵ | ❌¹³ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7238 (T1) | ✅ | ✅ | ✅⁷ | ✅ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7252 | ⚠️¹ | ✅³ | ✅⁷ | ✅ | ✅ | ⚠️¹ | ❌ | ⚠️²⁴ |
| BK7252N (T4) | ✅ | ✅ | ✅⁷ | ✅ | ✅ | ✅ | ❌ | ✅²⁴ |
| BK7258 (T5) | ✅ | ✅ | ⚠️²⁵ | ❌¹³ | ✅ | ✅ | ❌ | ✅²⁴ |
| RTL8710B (AmebaZ) | ✅ | ✅ | ✅⁸ | ✅ | ➖ | ⚠️¹⁸ | ❌ | ✅²⁴ |
| RTL87X0C (AmebaZ2) | ✅ | ✅ | ❌⁹ | ✅ | ➖ | ⚠️¹⁸ | ❌ | ✅²⁴ |
| RTL8720DN (AmebaD) | ✅ | ✅ | ✅⁸ | ✅ | ➖ | ⚠️¹⁸ | ❌ | ✅²⁴ |
| LN882H | ✅ | ✅ | ❌¹⁰ | ✅ | ➖ | ⚠️¹⁹ | ❌ | ✅²⁴ |
| LN8825 | ✅ | ✅ | ❌¹⁰ | ✅ | ➖ | ⚠️¹⁹ | ❌ | ✅²⁴ |
| BL602 | ✅ | ✅ | ✅⁸ | ✅ | ➖ | ⚠️²⁰ | ❌ | ✅²⁴ |
| BL702 | ✅ | ✅ | ✅⁸ | ❌¹⁴ | ➖ | ⚠️²⁰ | ❌ | ✅²⁴ |
| ECR6600 | ✅ | ✅ | ✅⁸ | ✅ | ➖ | ✅ | ❌ | ✅²⁴ |
| W800 | ✅ | ✅⁴ | ❌¹¹ | ✅¹⁵ | ➖ | ⚠️²¹ | ❌ | ✅²⁴ |
| W600 (write) | ❌² | ✅⁴ | ❌¹¹ | ⚠️¹⁶ | ➖ | ❌²² | ❌ | ⚠️²⁴ |
| RDA5981 | ✅ | ✅⁵ | ✅⁸ | ✅ | ➖ | ⚠️⁵ | ❌ | ✅²⁴ |
| Beken SPI CH341 | ✅ | ✅⁴ | ⚠️¹² | ❌ | ❌ | ❌²³ | ❌ | ⚠️²⁴ |
| Generic SPI CH341 | ✅ | ✅⁴ | ⚠️¹² | ❌ | ❌ | ❌²³ | ❌ | ⚠️²⁴ |

¹ BK7252 read operations are performed from **0x11000** (tool warns that bootloader is not accessible). Custom operations that target the bootloader area are therefore limited/unsupported.  
² W600 read is explicitly disabled (“W600 doesn't support read. Use JLink for firmware backup.”).  
³ For BK7231T/BK7231U/BK7252, default firmware write starts at **0x11000** (bootloader preserved). When writing “*_QIO_*” images, the tool may also skip the bootloader unless “Overwrite bootloader” is enabled.  
⁴ W800/W600 write is format/offset constrained: supports **.fls** directly, or a full backup-style **.bin** that contains a firmware header at **0x2000**; the tool generates a pseudo-FLS container for writing.  
⁵ RDA5981 firmware write uses a fixed start address (e.g., **0x0** or **0x1000**) based on image header, so custom write offsets are not supported (custom read still works).  
⁷ For Beken UART platforms (BK*), “Erase all” erases from **0x11000** onward (bootloader preserved).  
⁸ For these platforms, “Erase all” performs a full-flash / chip-erase style operation (bootloader not preserved).  
⁹ RTL87X0C (AmebaZ2) erase is not implemented in the RTLZ2 backend (doErase returns false).  
¹⁰ LN882H/LN8825 erase is not implemented (doErase returns false).  
¹¹ W800/W600 erase is not implemented (doErase returns false).  
¹² SPI CH341 erase supports **chip erase only** (no ranged erase).  
¹³ BK7236/BK7258: OBK config location is not defined in the tool’s OBKFlashLayout mapping, so config write (button and auto-inject) is unsupported/unsafe.  
¹⁴ BL702: OBK config write is explicitly blocked (“Config write is not supported on BL702”).  
¹⁵ W800: config location uses a **0x303 padding/header scheme** in the config write path.  
¹⁶ W600: “Write only OBK config” is disabled; config injection is supported only during a full firmware write via “Automatically configure OBK on flash write”.  
¹⁸ RTL custom operations are mixed: custom read converts byte offsets to sectors; custom write does not, so custom write offset handling is effectively unsupported.  
¹⁹ LN custom operations are mixed: custom read works; custom write offsets are ignored (write path targets platform-defined regions).  
²⁰ BL602/BL702 custom operations are mixed: custom read works; custom write offsets are ignored (write path targets platform-defined regions/partitions).  
²¹ W800 custom operations are mixed: custom read works; custom write offsets are ignored / write path is format constrained.  
²² W600: no read + fixed-offset write path; custom operations are not supported.  
²³ SPI CH341: custom operations are not supported (custom UI uses byte offsets, SPI backend expects sector indices).  
²⁴ Tuya config extraction is best-effort on the produced dump (or a dragged-in dump). Some helpers (e.g., MAC extraction from RF offsets) assume the dump is **0-based**; if a dump begins at a non-zero flash offset (e.g., BK7252 reads from 0x11000), derived offsets/MAC may be incorrect.  
²⁵ BK7236/BK7258: “Erase all” computes its sector count using a **2MB** constant at the UI level, so it does not cover the full flash on larger-flash parts.

✅ - Works  
⚠️ - Works with caveats/limitations  
❌ - Not implemented / disabled  
➖ - Not applicable
