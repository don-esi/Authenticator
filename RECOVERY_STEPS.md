# Authenticator Project Recovery Steps

## Current Situation

The build is failing due to package restore issues and file locks from Visual Studio. The WinUI 3 packages are not being resolved correctly, causing hundreds of compilation errors.

## Root Cause

1. **File Locks**: Visual Studio has locked `Microsoft.UI.Xaml.Markup.Compiler.dll` preventing clean rebuild
2. **Package Cache**: There may be corrupted or partial package downloads
3. **Project File Issues**: Some configuration properties may be causing conflicts

## Step-by-Step Recovery Process

### Step 1: Close Visual Studio
**IMPORTANT**: Close all instances of Visual Studio completely before proceeding.

### Step 2: Clean the Project
Open PowerShell in `D:\Source\Authenticator\` and run:

```powershell
# Remove build artifacts
Remove-Item -Recurse -Force obj, bin -ErrorAction SilentlyContinue

# Clear NuGet caches
dotnet nuget locals all --clear
```

### Step 3: Restore Packages
```powershell
dotnet restore --force
```

### Step 4: Reopen Solution
1. Open Visual Studio
2. Open `D:\Source\Authenticator\Authenticator.slnx`
3. Wait for IntelliSense to complete indexing

### Step 5: Build
```powershell
dotnet build
```

## Known Issues Fixed

✅ Removed invalid `Shadow` resource from `Styles/Colors.xaml`  
✅ Removed `Shadow` property setters from styles  
✅ Fixed duplicate `Content` properties in `AddAccountPage.xaml` buttons  
✅ Removed `InverseBoolConverter` reference (not defined)  
✅ Commented out external project references causing NU1105 errors  
✅ Removed problematic `CommunityToolkit.WinUI.Controls.Segmented` package  
✅ Removed `<Page Remove>` entries from csproj  
✅ Removed `<WinUISDKReferences>false</WinUISDKReferences>` property  

## Remaining Work After Successful Build

### 1. Fix MainWindow.xaml
Add the missing navigation elements:

```xml
<Window ...>
	<Grid>
		<NavigationView x:Name="NavView" 
						PaneDisplayMode="Bottom"
						SelectionChanged="NavView_SelectionChanged">
			<NavigationView.MenuItems>
				<NavigationViewItem Content="2FA" Tag="TwoFactor" />
				<NavigationViewItem Content="Backup" Tag="Backup" />
				<NavigationViewItem Content="Settings" Tag="Settings" />
			</NavigationView.MenuItems>

			<Frame x:Name="ContentFrame" />
		</NavigationView>
	</Grid>
</Window>
```

### 2. Implement TOTP Library
Since we removed the GoogleAuthenticator package reference, you need to either:

**Option A**: Restore the project reference (if available locally)
```xml
<ItemGroup>
  <ProjectReference Include="..\GoogleAuthenticator\Google.Authenticator\Google.Authenticator.csproj" />
</ItemGroup>
```

**Option B**: Use an alternative NuGet package
```xml
<PackageReference Include="Otp.NET" Version="1.4.0" />
```
Then update `Services/TotpService.cs` to use the new library.

**Option C**: Implement custom TOTP (see TOTP_IMPLEMENTATION.md for code)

### 3. Add Missing Converters
Create `Converters/InverseBoolConverter.cs`:

```csharp
using Microsoft.UI.Xaml.Data;

namespace Authenticator.Converters;

public class InverseBoolConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		return value is bool b && !b;
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		return value is bool b && !b;
	}
}
```

Then add to `AddAccountPage.xaml` resources:
```xml
<converters:InverseBoolConverter x:Key="InverseBoolConverter"/>
```

And restore the binding on the Save button:
```xml
<Button ... IsEnabled="{x:Bind ViewModel.IsSaving, Mode=OneWay, Converter={StaticResource InverseBoolConverter}}" />
```

### 4. Test MVVM Source Generation
After build succeeds, verify that `CommunityToolkit.Mvvm` generated properties:
- Check `obj/Debug/net8.0-windows10.0.19041.0/generated/` for generated files
- If properties still not found, add explicit properties instead of `[ObservableProperty]`

## Troubleshooting

### If restore still fails:
1. Check for leftover Visual Studio processes: `Get-Process | Where-Object {$_.ProcessName -like "*Visual*"}`
2. Kill any lingering processes: `Stop-Process -Name "ServiceHub.Host.dotnet.x64" -Force`
3. Delete `.vs` folder in solution directory
4. Try restoring from Developer Command Prompt for VS 2026

### If XAML files show "root element" errors:
- This is usually a build cache issue
- Clean rebuild should fix it
- If not, check file encoding (should be UTF-8 with BOM for XAML)

### If EF Core types not found:
- Ensure packages restored successfully
- Check `obj/project.assets.json` exists
- Verify `Microsoft.EntityFrameworkCore.Sqlite` version 8.0.11 is listed

## Package Versions (Current)

```xml
<PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.28000.2270" />
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.2.0" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.0.11" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="8.0.11" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.2" />
<PackageReference Include="WinUIEx" Version="2.4.2" />
<PackageReference Include="ZXing.Net" Version="0.16.9" />
<PackageReference Include="System.Drawing.Common" Version="8.0.11" />
```

## Expected Outcome

After following these steps:
1. ✅ Packages should restore without errors
2. ✅ WinUI 3 types should be available (Microsoft.UI.Xaml.*)
3. ✅ EF Core types should resolve
4. ✅ CommunityToolkit.Mvvm should generate properties
5. ⚠️ TOTP service will need implementation choice
6. ⚠️ MainWindow navigation will need XAML additions
7. ⚠️ Some converters may need to be added

## Next Development Steps (After Build Success)

1. Complete MainWindow navigation UI
2. Implement/integrate TOTP library
3. Create Backup and Settings pages
4. Implement QR code scanning with ZXing.Net
5. Test database initialization and CRUD operations
6. Implement docking/collapse window behavior with WinUIEx
7. Add PasswordManager integration (LastPass/Keypass tabs)
8. Polish UI animations and transitions
9. Add comprehensive error handling
10. Write unit tests

## Contact for Issues

If you encounter issues not covered here, check:
- Windows App SDK documentation: https://learn.microsoft.com/windows/apps/windows-app-sdk/
- WinUI 3 samples: https://github.com/microsoft/WindowsAppSDK-Samples
- EF Core docs: https://learn.microsoft.com/ef/core/
