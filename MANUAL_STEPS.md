# Manual steps

Most day-to-day work is covered by [README.md](README.md) and [QUICK_START.md](QUICK_START.md).

## Ship a Setup.exe

1. Install Inno Setup 6.4+.
2. Run `.\packaging\build-setup.ps1`.
3. Distribute `packaging\dist\AuthenticatorSetup-*.exe` (run elevated on the target PC).

## Local SQL override

Write `%LocalAppData%\Authenticator\connection.json` if you are not using `.\SQLEXPRESS`.
