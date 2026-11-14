/*
    Remove dbo.GetTotp and the SQL CLR assemblies from Authenticator.

    TOTP is client-side via Authenticator.Crypto (unlocked DEK + Otp.NET).
    Do not run from the app. Review, then SSMS or:
      sqlcmd -S .\ZOGEXPRESS2025 -E -i D:\Source\Authenticator\DBSchema\Drop_GetTotp.sql

    Adjust the server name if connection.json points elsewhere.
*/

USE Authenticator;
GO

IF OBJECT_ID(N'dbo.GetTotp', N'FS') IS NOT NULL
    DROP FUNCTION dbo.GetTotp;
GO

IF EXISTS (SELECT 1 FROM sys.assemblies WHERE name = N'Authenticator.SqlClr')
    DROP ASSEMBLY [Authenticator.SqlClr];
GO

IF EXISTS (SELECT 1 FROM sys.assemblies WHERE name = N'Otp.NET')
    DROP ASSEMBLY [Otp.NET];
GO

USE master;
GO

IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'AuthenticatorSqlClrLogin')
    DROP LOGIN AuthenticatorSqlClrLogin;
GO

IF EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE name = N'AuthenticatorSqlClrKey')
    DROP ASYMMETRIC KEY AuthenticatorSqlClrKey;
GO

IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'OtpNetSqlClrLogin')
    DROP LOGIN OtpNetSqlClrLogin;
GO

IF EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE name = N'OtpNetSqlClrKey')
    DROP ASYMMETRIC KEY OtpNetSqlClrKey;
GO
