# Dc07ProUAC

An unofficial, cross-platform USB Audio Class (UAC) control application  
for iBasso DAC devices, primarily tested with **DC07Pro**.

Supports **Windows, macOS, and Linux**.

---

## ✨ Features

- Cross-platform UAC control (Windows / macOS / Linux)
- Feature parity with the official Android UAC application
- Native desktop UI built with **Avalonia**
- HID-based communication using **HidSharp**
- No proprietary SDKs or vendor libraries
- Lightweight and dependency-minimal

---

## 🔌 Device Compatibility

### ✅ Tested
- **iBasso DC07Pro** — fully tested and confirmed working

### ⚠️ Likely Compatible (Untested / Partial)
- **iBasso DC06Pro**
- Other iBasso DACs using similar UAC/HID control schemes

> Compatibility with other devices depends on firmware and
> UAC/HID implementation details.

---

## 🧠 Implementation Notes

This project is a **clean-room, independent implementation**.

- Written in a different language from the original Android application
- UI designed and implemented independently
- Communication implemented via standard HID interfaces
- No original source code, assets, or proprietary SDKs were used

This project is **not affiliated with or endorsed by iBasso**.

---

## 🖥️ Platform Support

- Windows (x64)
- macOS (Apple Silicon / Intel)
- Linux (tested on common desktop distributions)

> Linux may require appropriate udev permissions for HID access.

---

## 📦 Build & Run

### Prerequisites
- .NET SDK (latest LTS recommended)

### Build
```bash
dotnet build
```

### Run
```bash
dotnet run
```
