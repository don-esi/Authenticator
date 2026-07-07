# Quick Action Checklist

## ⚠️ STOP - Read This First

Visual Studio has file locks preventing package restore. You **MUST** close Visual Studio before proceeding.

---

## Step-by-Step Actions (5 minutes)

### ☐ Action 1: Close Visual Studio
- Close all Visual Studio windows
- Wait 5 seconds
- Check Task Manager - kill any lingering `devenv.exe` or `ServiceHub` processes if needed

### ☐ Action 2: Clean Project
Open PowerShell in `D:\Source\Authenticator\`:
```powershell
Remove-Item -Recurse -Force obj, bin -ErrorAction SilentlyContinue
```

### ☐ Action 3: Restore Packages
```powershell
dotnet restore
```
✅ **Expected**: "Restore succeeded" message  
❌ **If error**: See RECOVERY_STEPS.md

### ☐ Action 4: Fix AddAccountPage.xaml
Open `Views\AddAccountPage.xaml` in any text editor and:

**A. Add converter to resources (line ~10):**

After the `</ResourceDictionary.MergedDictionaries>` line, add:
```xml
<converters:InverseBoolConverter xmlns:converters="using:Authenticator.Converters" x:Key="InverseBoolConverter"/>
```

**B. Fix Save button (line ~45-51):**

Change:
```xml
Margin="0,0,12,0" />
```

To:
```xml
Margin="0,0,12,0"
IsEnabled="{x:Bind ViewModel.IsSaving, Mode=OneWay, Converter={StaticResource InverseBoolConverter}}" />
```

### ☐ Action 5: Choose TOTP Option

**Easiest: Add Otp.NET package**

Edit `Authenticator.csproj`, in the `<ItemGroup>` with `PackageReference` items, add:
```xml
<PackageReference Include="Otp.NET" Version="1.4.0" />
```

Then copy the TotpService implementation from `TOTP_IMPLEMENTATION.md` section "Option 1".

**OR** restore GoogleAuthenticator project reference (if you have it locally).

**OR** implement custom TOTP (see TOTP_IMPLEMENTATION.md Option 3).

### ☐ Action 6: Restore Packages Again
```powershell
dotnet restore
```

### ☐ Action 7: Reopen Visual Studio
- Open `D:\Source\Authenticator\Authenticator.slnx`
- Wait for IntelliSense to finish loading (~30 seconds)

### ☐ Action 8: Build
Press `Ctrl+Shift+B` or:
```powershell
dotnet build
```

---

## ✅ Success Criteria

After Action 8, you should see:
```
Build succeeded.
	0 Warning(s)
	0 Error(s)
```

---

## ❌ If Build Fails

### Error: "Uno.WinUI not found"
```powershell
Remove-Item -Recurse -Force .vs -ErrorAction SilentlyContinue
dotnet build-server shutdown
```
Close VS, reopen, retry build.

### Error: "Microsoft.UI.Xaml types not found"
Check `obj/project.assets.json` exists. If not:
```powershell
dotnet restore --force
```

### Error: TOTP service compile errors
You haven't chosen a TOTP implementation option yet. See Action 5 above.

### Error: "ServiceName property not found in AddAccountViewModel"
CommunityToolkit.Mvvm source generation hasn't run yet. Try:
1. Clean solution
2. Rebuild solution
3. If still failing, check `obj/Debug/net8.0-windows10.0.19041.0/generated/` folder exists

### Other Errors
See `RECOVERY_STEPS.md` for comprehensive troubleshooting.

---

## 🎯 Next After Successful Build

1. Press `F5` to run the app
2. Click "Add Account" FAB button
3. Add a test 2FA account
4. Verify TOTP codes generate every 30 seconds
5. Test navigation tabs at bottom

---

## 📚 Reference Files

- `MANUAL_STEPS.md` - Detailed step-by-step with explanations
- `RECOVERY_STEPS.md` - Comprehensive troubleshooting guide
- `TOTP_IMPLEMENTATION.md` - Three TOTP library options with full code
- `README.md` - Original project overview

---

## Time Estimate

- Actions 1-8: **5 minutes**
- TOTP implementation: **2-10 minutes** (depending on option)
- Total: **7-15 minutes** to working build

---

**Start with Action 1 now** →
