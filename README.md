# FaceLock 🛡️
### Multi-Photo Physical Presence Protection via Face Recognition

FaceLock is a C# (.NET / WPF) utility that secures your workstation using your webcam. If you leave your desk or an unauthorized person sits in front of your computer, the application automatically triggers Windows Lock or Logoff.

---

## Features
- **Multi-Photo Owner Gallery**: Capture multiple snapshots of yourself from different angles, lighting conditions, or with/without glasses.
- **Match-Any Recognition**: If the live webcam face matches **ANY** of the enrolled pictures above the sensitivity threshold, access continues uninterrupted. If none match (or you step away), the computer locks/logs out.
- **Visual Photo Gallery & Management**:
  - Visually inspect enrolled face thumbnails and timestamps.
  - Select individual photos and click **"🗑️ Delete Selected"** to remove them.
  - Clear all photos option.
- **Strict Master Password Protection**:
  - When the master password is not entered, the 3-second arming countdown is **disabled**, and face recognition and locking are completely **inactive**.
  - Entering the correct master password unlocks the UI and immediately starts the 3-second grace countdown (3... 2... 1... System Armed).
  - Clicking **"🔒 Lock UI & Pause"** revokes authentication, locks the interface, and turns off auto-locking.
- **Configurable Action**:
  - **Lock Workstation (Default)**: Windows `LockWorkStation` (same as Win+L).
  - **User Logout (Optional)**: Invokes `ExitWindowsEx` to sign out.
- **Encrypted Local Storage**: Master password hashed with PBKDF2 (100,000 iterations); owner face gallery encrypted on disk using Windows DPAPI.

---

## How to Run

1. Open a terminal in `C:\Users\HP\source\repos\FaceLockApp`.
2. Run:
   ```bash
   dotnet run
   ```
3. Enter your master password (or create one on first launch).
4. Look into the webcam and click **"📷 Capture & Add Photo"** as many times as you like (e.g. front view, slight angles).
5. Protection automatically arms with the 3-second grace countdown!
