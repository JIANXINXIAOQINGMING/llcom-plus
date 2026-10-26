CH347 Windows vendor runtime
===========================

Copyright (C) 2001-2025 WCH / Nanjing Qinheng Microelectronics Co., Ltd.
These unmodified vendor binaries are not covered by llcom plus's Apache license.
No additional license grant from WCH is asserted by this project.

Version: 1.50 (both process architectures)
Retrieved: 2026-10-22
Official product, SDK and driver information:
https://www.waveshare.net/wiki/USB_TO_UART/I2C/SPI/JTAG
https://www.wch.cn/downloads/CH341PAR_ZIP.html

x64 source:
https://files.waveshare.com/wiki/USB-TO-UART-I2C-SPI-JTAG/Demo/CH347DLLA64.DLL
SHA256: D08C1CCD0146376A55602D9F2038DABC39C36AF95CF7A36F11B17D42F8420388

x86 source (identical to CH347Demo/CH347Demo/lib/CH347DLL.dll in the official C# sample):
https://files.waveshare.com/wiki/USB-TO-UART-I2C-SPI-JTAG/Demo/CH347DLL-CS2.DLL
SHA256: 7C9F762A5F379928ED99DA8F2FE4C6128139A8331CE8D89A6F0E98317401A407

Both source files had a valid Authenticode signature from Microsoft Windows
Hardware Compatibility Publisher when imported. Each build verifies the pinned
SHA256 hash and copies only the DLL matching the application's process architecture
to CH347/CH347DLL.dll. Kernel drivers and vendor demo applications are not installed
or run by llcom plus. Install the vendor driver yourself when using M1/M3 mode.

The native API declarations are checked against CH347DLL.H V1.5 from:
https://files.waveshare.com/wiki/USB-TO-UART-I2C-SPI-JTAG/Demo/CH347_I2CDemo_C%23.zip
SPI_CONFIG uses pack(1), size 20. DEV_INFOR uses pack(1), size 541 (x86) / 545 (x64).
CH347OpenDevice returns a pointer-sized HANDLE; Win32 BOOL and ULONG remain 32 bits.
JTAG uses CH347Jtag_SwitchTapStateEx(index, state), not the legacy implicit-device
CH347Jtag_SwitchTapState(state). Transfers use CH347Jtag_IoScanT with packed,
LSB-first data, an exact bit count, readback enabled, and the last-packet flag set;
the SDK handles the final data bit's TMS transition. State 6 updates IR/DR and
returns from Exit1 to Run-Test/Idle. Both shipped DLLs export these functions.
CH347Jtag_INIT resets the target TAP and returns it to Idle. Initialization is
therefore explicit and must not be repeated before each IR/DR shift. The tool
requires a new TAP reset after a clock change, another bus operation, or an error.
Only clock codes 0-5 are exposed (468.75 kHz through 15 MHz), even though the V1.5
header also describes codes 6/7. TRST and SRST are not driven by this tool.
The device-information FuncType values for JTAG are 2 (JTAG/I2C) and 3
(JTAG/I2C/SPI); ChipMode 3 is CH347T M3 and ChipMode 4 is CH347F.
The page uses the SDK rather than replacing a device's driver with WinUSB or resetting
the composite USB device. Unsupported/absent libraries disable only this tool.
