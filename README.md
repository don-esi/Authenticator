# Authenticator - WinUI 3 Desktop App

## Overview
A comprehensive Windows desktop authenticator application built with WinUI 3, replicating modern Fluent Design with live TOTP (Time-based One-Time Password) generation, folder organization, backup codes management, and planned integration with password managers.

## Current Status: Foundation Complete (MVP in Progress)

### ✅ **Implemented Features**

#### Core Infrastructure
- **Database Layer** (EF Core + SQLite)
  - Models: Account, Folder, BackupCode, PasswordEntry, AppSettings
  - AuthenticatorDbContext with relationships and indexes
  - DatabaseInitializer for automatic setup at %LOCALAPPDATA%\Authenticator\authenticator.db
  - DatabaseService with full CRUD operations

- **TOTP Service**
  - Integration with GoogleAuthenticator library (NuGet package)
  - Support for SHA1/SHA256/SHA512 algorithms
  - Configurable digits (6/8) and periods (15/30/60/120 seconds)
  - Live code generation with progress tracking

- **Services**
  - TotpService: Code generation, validation, progress calculation
  - DatabaseService: All entity CRUD operations, soft delete, trash management
  - SettingsService: App preferences with caching
  - QrCodeService: Foundation for QR decoding (ZXing.Net integration)

#### UI Components & Styling
- **Design System** matching reference images
  - Colors.xaml: Exact brand blue (#0078D4), pastel folder colors, light backgrounds
  - Styles.xaml: Card, button, text, search box styles with Fluent Design
  - Animations.xaml: Fade, slide, scale, expand/collapse transitions

- **Reusable Controls**
  - AccountCard: Service icon, name, username, live TOTP code, progress bar, actions
  - FolderChip: Colored folder buttons with account count badges
  - TotpProgressBar: Thin blue animated countdown indicator

- **Value Converters**
  - FirstLetterConverter, StringToColorConverter, CountToVisibilityConverter
  - ProgressToWidthConverter, BoolToVisibilityConverter, InverseBoolToVisibilityConverter
  - ZeroToVisibilityConverter, ViewModeIconConverter

#### ViewModels (MVVM with CommunityToolkit.Mvvm)
- **TwoFactorViewModel**
  - Live TOTP updates with DispatcherQueueTimer (1-second intervals)
  - Search filtering, folder filtering, sorting (6 options)
  - Account management commands: Add, Edit, Delete, Copy, ShowBackupCodes
  - List/Grid view toggle

- **AddAccountViewModel**
  - Form validation for service name, username, secret key
  - Advanced options: Algorithm, Digits, Period selection
  - Backup codes import from text/file
  - QR code result loading

- **AccountItemViewModel**
  - Live binding wrapper for accounts with current code, progress, seconds remaining

#### Pages & Navigation
- **MainWindow**
  - NavigationView with bottom pane (3 tabs: 2FA, Backup, Settings)
  - Default size: 778x1490 (narrow tall portrait mode)
  - WinUIEx integration for window management

- **TwoFactorPage**
  - Search box with rounded pill style
  - View toggle (list/grid) and sort dropdown
  - Horizontal scrolling folder chips
  - Filterable/sortable accounts with AccountCard controls
  - Empty state with call-to-action
  - Floating Action Button (FAB) for adding accounts

- **AddAccountPage**
  - Form with service name, URL, username, secret key fields
  - Advanced Options expander (Algorithm/Digits/Period)
  - Backup Codes expander with multi-line input and file import
  - Top bar with back button and Save button

### 📦 **NuGet Packages**
- Microsoft.EntityFrameworkCore.Sqlite 8.0.11
- Microsoft.EntityFrameworkCore.Tools 8.0.11
- CommunityToolkit.Mvvm 8.3.2
- WinUIEx 2.4.2
- ZXing.Net 0.16.9
- System.Drawing.Common 8.0.11
- GoogleAuthenticator 3.4.0 (TOTP generation)

### ⚠️ **Known Build Issues (To Fix)**

1. **XAML Parsing Errors**
   - Some XAML files may have been truncated during creation
   - Need to verify all UserControl and Page files have proper closing tags
   - Shadow resource in Colors.xaml incompatible with WinUI 3 (remove or replace with ThemeShadow)

2. **CommunityToolkit.Mvvm Code Generation**
   - `[ObservableProperty]` fields not generating properties for x:Bind
   - May need to rebuild or clean solution to trigger source generators
   - Alternative: manually implement properties with `INotifyPropertyChanged`

3. **Package Version Issues**
   - CommunityToolkit.WinUI.UI.Controls 8.1.240916 not found
   - Downgrade to 7.1.2 or remove if not critical

4. **Project References**
   - External projects (GoogleAuthenticator, PasswordManager) not in solution
   - Replaced GoogleAuthenticator with NuGet package (GoogleAuthenticator 3.4.0)
   - PasswordManager integration deferred to future iteration

### 🚧 **Deferred Features (Post-MVP)**

#### Phase 2: Advanced Features
- **QR Code Scanning**
  - Windows.Graphics.Capture API for screenshot capture
  - ZXing.Net barcode decoder (foundation in place)
  - otpauth:// URI parser (foundation complete)
  - QrScanPage UI with crop rectangle and preview

- **Backup Codes Management**
  - BackupCodesPage listing services with code counts
  - Detail view with copy buttons and mark-as-used
  - Edit mode with file import
  - Database models and services complete

- **Settings Page**
  - Data Storage section with live counts
  - Export & Import functionality
  - Auto Lock timeout selection (30s to Never)
  - "Sync Time with Internet" toggle with NTP client
  - SettingsService complete

- **Password Manager Integration**
  - LastPass tab with credential listing
  - Keypass tab with secure storage
  - Import flows linking passwords to 2FA accounts
  - Requires PasswordManager library integration

#### Phase 3: Advanced Window Behavior
- **Dockable Window**
  - Edge snapping (left/right monitor edges)
  - Blue grab handle (30-40px wide, full height)
  - Auto-collapse/expand animations
  - Persist docked state and position

#### Phase 4: Polish & Enhancements
- Page transition animations (slide, fade)
- Hover effects on buttons/cards
- Loading states and progress indicators
- Toast notifications
- Service logos/icons (fallback to colored circles with initials)
- Create Folder flow with color swatches and multi-select
- Move to Folder functionality
- Favorites support

## Build Instructions

### Prerequisites
- Visual Studio 2022 (v17.7+) or VS 2026
- .NET 8.0 SDK
- Windows App SDK 2.2.0
- Windows 10 SDK (10.0.19041.0 or higher)

### Steps to Build
1. **Open Solution**
   ```
   Open Authenticator.slnx in Visual Studio
   ```

2. **Restore NuGet Packages**
   ```bash
   dotnet restore
   ```

3. **Fix Known Issues**
   - Check all XAML files for proper closing tags
   - Remove or comment out Shadow resource in `Styles/Colors.xaml`
   - Downgrade `CommunityToolkit.WinUI.UI.Controls` to 7.1.2 or remove
   - Rebuild solution to trigger CommunityToolkit.Mvvm source generators

4. **Build**
   ```bash
   dotnet build
   ```

5. **Run**
   ```bash
   dotnet run
   ```

### Manual Path Adjustments
If using the external project references (commented out by default):
- Ensure `D:\Source\GoogleAuthenticator\Google.Authenticator\Google.Authenticator.csproj` exists
- Ensure `D:\Source\WealthForge\API\PasswordManager\PasswordManager.csproj` exists
- Uncomment the `<ProjectReference>` items in Authenticator.csproj

Alternatively, use the GoogleAuthenticator NuGet package (already configured).

## Project Structure

```
Authenticator/
├── Data/
│   ├── Models/                 # Entity classes
│   │   ├── Account.cs
│   │   ├── Folder.cs
│   │   ├── BackupCode.cs
│   │   ├── PasswordEntry.cs
│   │   └── AppSettings.cs
│   ├── AuthenticatorDbContext.cs
│   └── DatabaseInitializer.cs
├── Services/
│   ├── TotpService.cs          # TOTP code generation
│   ├── DatabaseService.cs      # CRUD operations
│   ├── SettingsService.cs      # App preferences
│   └── QrCodeService.cs        # QR scanning (foundation)
├── ViewModels/
│   ├── TwoFactorViewModel.cs   # Main 2FA screen
│   ├── AddAccountViewModel.cs  # Add/Edit account
│   └── AccountItemViewModel.cs # Account wrapper for live updates
├── Views/
│   ├── TwoFactorPage.xaml      # Main 2FA UI
│   ├── AddAccountPage.xaml     # Add/Edit form
│   └── (Future: QrScanPage, BackupCodesPage, SettingsPage)
├── Controls/
│   ├── AccountCard.xaml        # Account display with live TOTP
│   ├── FolderChip.xaml         # Folder button
│   └── TotpProgressBar.xaml    # Progress indicator
├── Converters/
│   └── ValueConverters.cs      # Data binding converters
├── Styles/
│   ├── Colors.xaml             # Color palette
│   ├── Styles.xaml             # Component styles
│   └── Animations.xaml         # Transitions
├── MainWindow.xaml             # App window with navigation
├── App.xaml                    # Application resources
└── Authenticator.csproj
```

## Key Design Decisions

### TOTP Generation
- Uses GoogleAuthenticator NuGet package (Brandon Potter's library)
- Supports standard otpauth:// URI format
- Live updates every second via DispatcherQueueTimer
- Configurable algorithm (SHA1/SHA256/SHA512), digits (6/8), period (15/30/60/120s)

### Database
- SQLite for local-only storage (no cloud sync in MVP)
- Stored at `%LOCALAPPDATA%\Authenticator\authenticator.db`
- EF Core with code-first migrations
- Soft delete pattern for accounts and password entries

### UI/UX
- Follows Fluent Design principles
- Exact colors from reference images:
  - Brand Blue: #0078D4
  - Pastel folders: teal, mint, periwinkle, coral, orange, purple
  - Light backgrounds: #F8F9FA, #FFFFFF
- Rounded corners (8px cards, 20px pills)
- Subtle shadows for elevation
- Large, readable monospace TOTP codes

### Architecture
- MVVM with CommunityToolkit.Mvvm
- Service layer for business logic
- Repository pattern via DatabaseService
- ObservableCollection + x:Bind for reactive UI

## Reference Images
Located in `Reference Images/` folder:
- MainScreenNarrow/Square/Wide.png - Main 2FA layouts
- MainScreenSort.png - Sort options
- CreateAccount-Part1/Part2.png - Add account flow
- AddQRCodeScreen.png - QR scanning
- CreateFolder.png - Folder creation
- BackupCodes-Part1/Part2/Part3.png - Backup codes management
- Settings.png, Settings-AutoLock.png - Settings screen
- Favorite.png, ShareShowQRCode.png - Additional features

## Future Enhancements
- NTP time synchronization
- Multi-device sync (cloud storage)
- Biometric unlock
- Browser extension integration
- Bulk import/export (JSON, CSV)
- Dark theme support
- Accessibility improvements
- Localization

## Contributing
This is a single-developer project. For major changes, please open an issue first to discuss what you would like to change.

## License
[Specify your license]

## Credits
- GoogleAuthenticator library by Brandon Potter
- PasswordManager library (for future integration)
- WinUI 3 by Microsoft
- CommunityToolkit.Mvvm by Microsoft
- ZXing.Net for QR code scanning
