# Final Manual Steps Required

## CRITICAL: You Must Close Visual Studio First

The build system has file locks that prevent package restore. Follow these steps **in order**:

## Step 1: Close Visual Studio
Close all Visual Studio instances completely.

## Step 2: Clean Build Artifacts
Open PowerShell in `D:\Source\Authenticator\` and run:

```powershell
Remove-Item -Recurse -Force obj, bin -ErrorAction SilentlyContinue
dotnet restore
```

## Step 3: Add Missing Converter to AddAccountPage.xaml

Open `Views\AddAccountPage.xaml` and update the `Page.Resources` section:

### Find this:
```xml
<Page.Resources>
	<ResourceDictionary>
		<ResourceDictionary.MergedDictionaries>
			<ResourceDictionary Source="/Styles/Colors.xaml" />
			<ResourceDictionary Source="/Styles/Styles.xaml" />
		</ResourceDictionary.MergedDictionaries>
	</ResourceDictionary>
</Page.Resources>
```

### Replace with:
```xml
<Page.Resources>
	<ResourceDictionary>
		<ResourceDictionary.MergedDictionaries>
			<ResourceDictionary Source="/Styles/Colors.xaml" />
			<ResourceDictionary Source="/Styles/Styles.xaml" />
		</ResourceDictionary.MergedDictionaries>
		<converters:InverseBoolConverter xmlns:converters="using:Authenticator.Converters" x:Key="InverseBoolConverter"/>
	</ResourceDictionary>
</Page.Resources>
```

### And restore the Save button binding:

Find:
```xml
<Button Grid.Column="2"
		Content="Save"
		Click="SaveButton_Click"
		Style="{StaticResource PrimaryButtonStyle}"
		Padding="20,8"
		Margin="0,0,12,0" />
```

Replace with:
```xml
<Button Grid.Column="2"
		Content="Save"
		Click="SaveButton_Click"
		Style="{StaticResource PrimaryButtonStyle}"
		Padding="20,8"
		Margin="0,0,12,0"
		IsEnabled="{x:Bind ViewModel.IsSaving, Mode=OneWay, Converter={StaticResource InverseBoolConverter}}" />
```

## Step 4: Choose TOTP Implementation

You have 3 options (see `TOTP_IMPLEMENTATION.md` for details):

### Option A: Add Otp.NET Package (Easiest)

Add to `Authenticator.csproj` in the `<ItemGroup>` with other packages:
```xml
<PackageReference Include="Otp.NET" Version="1.4.0" />
```

Then replace the contents of `Services\TotpService.cs` with the code from `TOTP_IMPLEMENTATION.md` Option 1.

### Option B: Restore GoogleAuthenticator Project Reference

If you have the project locally, uncomment these lines in `Authenticator.csproj`:
```xml
<ItemGroup>
  <ProjectReference Include="..\GoogleAuthenticator\Google.Authenticator\Google.Authenticator.csproj" />
</ItemGroup>
```

The existing `TotpService.cs` should work as-is.

### Option C: Custom Implementation (No Dependencies)

Follow the instructions in `TOTP_IMPLEMENTATION.md` Option 3 to create:
- `Helpers/Base32Encoding.cs`
- `Helpers/HmacGenerator.cs`
- Updated `Services/TotpService.cs`

## Step 5: Reopen and Build

1. Open Visual Studio
2. Open `D:\Source\Authenticator\Authenticator.slnx`
3. Wait for restore to complete
4. Build the solution (Ctrl+Shift+B)

## Expected Results

After these steps:
- ✅ WinUI 3 packages should restore correctly
- ✅ EF Core types should be available
- ✅ CommunityToolkit.Mvvm source generation should work
- ✅ TOTP service should compile (after choosing option)
- ✅ All XAML files should compile
- ✅ Zero build errors (or only TOTP-related if you haven't chosen an option)

## What's Working Now

Files that have been fixed:
- ✅ `Authenticator.csproj` - cleaned up packages and references
- ✅ `Styles/Colors.xaml` - removed invalid Shadow resource
- ✅ `Styles/Styles.xaml` - removed Shadow property setters
- ✅ `Converters/ValueConverters.cs` - added InverseBoolConverter
- ✅ `Converters/InverseBoolConverter.cs` - standalone converter file created
- ✅ `MainWindow.xaml` - has ContentFrame and NavView elements
- ✅ All data models, services, viewmodels, and controls

## What Still Needs Work (After Build Succeeds)

1. **TOTP Implementation** - Choose and implement one of the 3 options
2. **Backup Page** - Create `Views/BackupPage.xaml` for backup codes management
3. **Settings Page** - Create `Views/SettingsPage.xaml` for app configuration
4. **QR Scanning** - Implement screen capture + ZXing.Net decoding
5. **PasswordManager Integration** - LastPass/Keypass tabs (deferred)
6. **Docking/Collapse** - Use WinUIEx for window edge docking
7. **UI Polish** - Animations, smooth transitions, visual feedback
8. **Testing** - Database operations, TOTP generation, UI flows

## If Build Still Fails

### Check package restore output:
```powershell
dotnet restore --verbosity detailed
```

### Look for:
- ✅ "Restored D:\Source\Authenticator\Authenticator.csproj"
- ✅ "Assets file has not changed. Skipping assets file writing..."
- ❌ Any NU1xxx errors
- ❌ Any "not found" messages

### Common Issues:

**"Uno.WinUI not found"**
- Delete `.vs` folder in solution directory
- Delete `obj` and `bin` folders
- Close VS and retry

**"Microsoft.UI.Xaml types not found"**
- Verify `Microsoft.WindowsAppSDK` version 2.2.0 is restored
- Check `obj/project.assets.json` exists
- Try `dotnet build-server shutdown` then reopen VS

**"EF Core types not found"**
- Verify `Microsoft.EntityFrameworkCore.Sqlite` 8.0.11 is restored
- Check NuGet package sources include nuget.org

## Documentation Files Created

1. **RECOVERY_STEPS.md** - Full recovery process and troubleshooting
2. **TOTP_IMPLEMENTATION.md** - Three options for TOTP library implementation
3. **MANUAL_STEPS.md** (this file) - Quick checklist for immediate actions

## Next Steps After Successful Build

1. Run the app (F5)
2. Verify database creates at `%LOCALAPPDATA%\Authenticator\authenticator.db`
3. Add a test account with a known TOTP secret
4. Verify code generation works
5. Test navigation between tabs
6. Begin implementing remaining features

## Support

If you encounter issues:
1. Check `RECOVERY_STEPS.md` for detailed troubleshooting
2. Review `TOTP_IMPLEMENTATION.md` for TOTP options
3. Verify all manual steps in this file were completed
4. Check Visual Studio Output window for specific errors
