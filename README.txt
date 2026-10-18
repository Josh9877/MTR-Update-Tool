MTR Manual Update Tool
======================

BUILD (only once - you do NOT rebuild for new MTR versions)
  1. Keep these files together in one folder:
       build.bat, Program.cs, app.manifest, app.ico, logo.png
  2. Double-click build.bat  ->  MTR-Update-Tool.exe  (small, a few hundred KB)

USE (each MTR update)
  1. Click "Get latest script..." in the tool (opens Microsoft's manual-update page; Step 1 has
     the download link) and download the new MTR-Update-x.x.x.x.ps1.
  2. Run MTR-Update-Tool.exe on the MTR device and accept the UAC prompt.
  3. Drop the .ps1 onto the window, or click Browse...
     (A MTR-Update-*.ps1 next to the EXE or in Downloads is pre-selected automatically.)
  4. Click "Start Update". Progress bar + live PowerShell output are shown.

  If PowerShell shows the "[R] Run once" prompt it is answered with R automatically
  (untick the box to disable). You can also type into "Send input" or press "Send R".
  "Save output..." writes the console output to a text file.

WHAT IT DOES
  - Runs elevated (manifest = requireAdministrator), so the script's own self-elevation
    (which would open a separate window) is skipped and output stays in the GUI.
  - Copies the chosen script byte-for-byte (it is Microsoft-signed; never modified) to
    C:\ProgramData\MTRUpdateTool_xxxxxxxx\ (Admins/SYSTEM-only ACL, safe file name, no spaces)
    and runs:  powershell.exe -ExecutionPolicy Unrestricted "<that path>"   (64-bit PowerShell)
  - Deletes the copy when finished. The original file is never touched.

NOTES
  - It runs whatever .ps1 you give it with admin rights - only use the script from Microsoft.
  - Drag & drop from a normal Explorer window into an elevated window is normally blocked by
    Windows; the tool works around that, but if a drop is ignored just use Browse...
  - Progress % is estimated from the script's log messages, so it moves in steps.
  - The EXE is unsigned, so SmartScreen/AV may warn; sign it with your code-signing cert if needed.

BRANDING
  app.ico = EXE/window/taskbar icon, logo.png = header logo. Replace (same names) and rebuild.
