```
███████╗ ██████╗ ███████╗████████╗ ██████╗██╗   ██╗██████╗ ███████╗███████╗
██╔════╝██╔═══██╗██╔════╝╚══██╔══╝██╔════╝██║   ██║██╔══██╗██╔════╝██╔════╝
███████╗██║   ██║█████╗     ██║   ██║     ██║   ██║██████╔╝███████╗█████╗  
╚════██║██║   ██║██╔══╝     ██║   ██║     ██║   ██║██╔══██╗╚════██║██╔══╝  
███████║╚██████╔╝██║        ██║   ╚██████╗╚██████╔╝██║  ██║███████║███████╗
╚══════╝ ╚═════╝ ╚═╝        ╚═╝    ╚═════╝ ╚═════╝ ╚═╝  ╚═╝╚══════╝╚══════╝
          S O F T C U R S E   V A U L T   E N G I N E
               The Mechanism Beneath the System
```

 
*A multi-module Windows optimization suite forged in the depths of the system vaults.*

Softcurse Vault Engine is an advanced WPF-based toolkit designed to purge, analyze, and optimize Windows environments.  
Forged with dark neon aesthetics and powered by modular architecture, the Engine provides deep cleanup, disk analysis, and system-level utilities with precision and style.

---

# 🌑 Core Modules

## 1. Vault Cleaner
Advanced cleanup subsystem that removes unnecessary files, caches, logs, and system debris.

### Features
- **Recycle Bin Purge**
- **System & User TEMP Cleanup**
- **Browser Cache Removal** (Chrome, Edge, Firefox, Brave)
- **Windows Update Cleanup**
- **Microsoft Store Cache Reset**
- **Python PIP Cache Cleanup**
- **Graphics Driver Cache Purge** (NVIDIA, AMD, Intel)
- **Unreal Engine Derived Data Cache Cleanup**
- **Android SDK System Image Cleanup**
- **Event Log Purging**
- **Font Cache Rebuild**
- **DISM Component Store Cleanup (ResetBase)**
- **Old Installer Removal** (MSI/MSP older than 6 months)
- **Thumbnail Cache Cleanup**

### Advanced Capabilities
- **Quick Scan** — Estimate recoverable space
- **Pagefile Relocation** — Move paging file to another drive
- **System Restore Relocation** — Free C: drive storage
- **Detailed Progress & UI Feedback**
- **Full Logging Pipeline** (`D:\VaultHunterLogs`)
- **Async operations** (UI never freezes)

---

## 2. WinDir Disk Analyzer
A standalone subsystem for deep disk inspection, visual analysis, and file forensics.

### Key Features
- **Full filesystem tree scan**
- **Top Files Explorer**
- **Top Directories Analysis**
- **Extension-Based Category Mapping**
- **Duplicate Finder**
- **Large File Hunter**
- **Aged File Analysis**
- **Real-time scan progress with circular neon indicator**
- **Detailed recommendations output**
- **Standalone HTML report generation**

WinDir opens as an independent vault window while inheriting the main UI theme.

---

# 🔧 Architecture Overview

Softcurse Vault Engine is fully modular and future-proof, built with the following principles:

### ✔ MVVM Pattern  
Strict ViewModel-driven architecture ensures clean separation of UI and logic.

### ✔ Independent Subsystems  
Cleaner, Disk Analyzer, and future modules run in isolation.

### ✔ Shared UI Theme  
A global resource dictionary unifies the application's neon aesthetic across all windows.

### ✔ Async/Task-Based Engine  
Long-running operations never block the UI thread.

### ✔ Global Logging Layer  
All modules write into the Softcurse Vault log system.

---

# 🧩 Planned Modules (v3.x Roadmap)

- **Startup & Services Manager**
- **Deep Uninstaller**
- **System Optimizer Panel (Tweaks)**
- **Network Insight Tool**
- **Disk Health & SMART Monitor**
- **Registry Backup & Cleanup**
- **AppData Forensics Scanner**

Each module is implemented as a standalone vault window using the shared Softcurse theme.

---

# 🖥 Requirements

- **Windows 10 or Windows 11**
- **.NET 6.0 Windows Desktop Runtime**
- **Administrator privileges** (recommended)
- **~1 MB disk space** for the engine itself

---

# 🛠 Building From Source

### Prerequisites
1. Install **.NET 6.0 SDK** or newer
2. Install **Visual Studio 2022** (or VS Code with C# extensions)

### Build

```powershell
cd "d:\Projects\Completed\Softcurse Vault Engine"
dotnet restore "VaultEngine\VaultEngine.csproj"
dotnet build "VaultEngine\VaultEngine.csproj" --configuration Release
dotnet run --project "VaultEngine\VaultEngine.csproj"
````

### Output

```
VaultEngine/bin/Release/net6.0-windows/SoftcurseVaultEngine.exe
```

---

# 🧭 Usage

### Vault Cleaner

1. Launch as Administrator
2. Configure cleanup options
3. Run **Quick Scan**
4. Run **Initiate Cleanup Protocol**
5. Review freed storage and logs

### WinDir Disk Analyzer

1. Open **Disk Analyzer** tab
2. Select drive or folder
3. Choose **Quick** or **Deep scan**
4. Watch neon circular progress indicator
5. Browse results or export HTML report

---

# ⚠ Safety Guidelines

* Cleanup operations are destructive — **no undo**
* System-level actions may require reboot
* Always review quick scan results
* Avoid scanning protected system folders unless necessary
* Pagefile changes require restart

---

# 📄 Logs

All operations are logged:

```
D:\VaultHunterLogs\vault-cleaner-YYYYMMDD-HHmmss.log
D:\VaultHunterLogs\widir-YYYYMMDD-HHmmss.log
```

Error logs:

```
D:\VaultHunterLogs\errors\*.log
```

---

# 🏗 Project Structure

```
VaultEngine/
├── App.xaml
├── MainWindow.xaml
├── MainWindowViewModel.cs
├── Modules/
│   ├── Cleaner/
│   │   ├── CleanerService.cs
│   │   ├── CleanerView.xaml
│   ├── WinDir/
│   │   ├── WinDirWindow.xaml
│   │   ├── TreeBuilder.cs
│   │   ├── Aggregator.cs
│   │   ├── HtmlReportBuilder.cs
│   │   ├── Models/
│   │   │   ├── FSNode.cs
│   │   │   ├── DuplicateItem.cs
│   │   │   ├── LargeFileItem.cs
│   │   │   ├── ExtensionStats.cs
│   ├── Shared/
│       ├── Controls/
│       ├── Themes/
│       ├── Helpers/
└── VaultEngine.csproj
```

---

# 🧬 Version History

### **v3.0 (Current — Softcurse Vault Engine)**

* Renamed project to Vault Engine
* Added full WinDir Disk Analyzer subsystem
* Added neon circular progress indicator
* Added duplicate finder & large file hunter
* Modular architecture introduced
* Shared UI theme system added

### **v2.2 (Old — Vault Cleaner)**

* MVVM refactor
* CleanerService abstraction
* Quick Scan
* Improved error handling

---

# 💀 Credits

**Softcurse Vault Engine**
Forged in WPF (.NET 6) using MVVM and dark neon aesthetics.

```
