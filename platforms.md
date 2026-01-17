# Easy Flasher – Platform and Feature Support

This document describes **Easy Flasher / BK7231GUIFlashTool** feature support by platform.
It is a *tool* support matrix (read/write/erase/config extraction), not an OpenBeken firmware feature matrix.

Legend:
✅ Works
⚠️ Partial / caveats apply
❌ Not supported / not implemented
➖ Not applicable

## Supported chip families (per project README)

- Beken
- BL602
- ECR6600
- LN882H
- RDA5981
- RTL8710B
- RTL8710C / RTL8720C (AmebaZ2)
- RTL8720D / RTL8720CS (AmebaD / AmebaCS)
- W800
- W600 (write only)

## Support matrix (tool functions)

| Platform / Mode | Family | Read | Write | Erase | OBK config write | RF restore | Custom read/write | RF relocation | Tuya config extraction |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| BK7231T | Beken | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| BK7231U | Beken | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| BK7231N | Beken | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| BK7231M | Beken | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| BK7238 | Beken | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| BK7252 | Beken | ⚠️ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ⚠️ |
| BK7252N | Beken | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| BK7236 | Beken | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ❌ | ✅ |
| BK7258 | Beken | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ❌ | ✅ |
| BL2028N | Beken | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ✅ |
| BL602 | Bouffalo | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ❌ | ✅ |
| BL702 | Bouffalo | ✅ | ✅ | ✅ | ❌ | ➖ | ✅ | ❌ | ✅ |
| BL616 | Bouffalo | ❌ | ❌ | ❌ | ❌ | ➖ | ❌ | ❌ | ❌ |
| RTL8710B | Realtek | ✅ | ✅ | ✅ | ✅ | ➖ | ⚠️ | ❌ | ✅ |
| RTL87X0C (RTL8710C/RTL8720C) | Realtek | ✅ | ✅ | ✅ | ✅ | ➖ | ⚠️ | ❌ | ✅ |
| RTL8720D | Realtek | ✅ | ✅ | ✅ | ✅ | ➖ | ⚠️ | ❌ | ✅ |
| RTL8721DA | Realtek | ✅ | ✅ | ✅ | ❌ | ➖ | ⚠️ | ❌ | ✅ |
| RTL8720E | Realtek | ✅ | ✅ | ✅ | ❌ | ➖ | ⚠️ | ❌ | ✅ |
| LN882H | Lightning Semi | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ❌ | ✅ |
| LN8825 | Lightning Semi | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ❌ | ✅ |
| ECR6600 | ESWIN | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ❌ | ✅ |
| RDA5981 | RDA | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ❌ | ⚠️ |
| W800 | WinnerMicro | ✅ | ✅ | ❌ | ✅ | ➖ | ✅ | ❌ | ⚠️ |
| W600 | WinnerMicro | ❌ | ✅ | ❌ | ⚠️ | ➖ | ⚠️ | ❌ | ❌ |
| BekenSPI (CH341 SPI) | Tool mode | ✅ | ✅ | ⚠️ | ❌ | ❌ | ✅ | ❌ | ✅ |
| GenericSPI (CH341 SPI) | Tool mode | ✅ | ✅ | ⚠️ | ❌ | ❌ | ✅ | ❌ | ✅ |

## Notes / caveats

- **BK7252 Read = ⚠️**: read is performed from a non-zero offset (bootloader region is not accessible in the same way), so “full flash dump” behavior differs vs other Beken targets.
- **OBK config write = ❌** on some Beken variants (e.g., BK7236/BK7258/BL2028N) because the tool’s config-location mapping is not defined for those chip IDs (so “write-only-config” / “auto-config write” is not safe/implemented).
- **BL702 OBK config write = ❌**: explicitly blocked by the BL flasher code path (tool reports config write not supported for BL702).
- **BL616 = ❌**: present in platform enums, but not wired into the active flasher selection; selecting it will not use the correct flasher backend.
- **LN882H/LN8825 Erase = ❌**: explicit “erase” operation is not implemented for LN; writing still works through the normal write flow.
- **W600 Read = ❌**: repo explicitly documents W600 as “write only”. W600 also blocks “Only OBK config” writes; config injection is intended via “auto-config during flash write”.
- **SPI erase = ⚠️**: SPI mode supports **chip erase** (full) but not general-purpose ranged erase.
- **Custom read/write = ⚠️ for RTL/W600**: RTL backends operate in sector-based addressing internally; custom operations are supported but have stricter expectations around alignment/units.
- **Tuya config extraction** is best-effort: it is performed by scanning the dumped binary for Tuya “magic” and attempting decrypt/parse; for some targets (notably W800/BK7252/RDA5981), this can be dependent on the specific Tuya keying/layout present in the dump.
- **RF relocation support = ❌**: there is RF *restore* logic for specific Beken flash layouts, but no general “relocate RF partition” feature implemented by the tool.
