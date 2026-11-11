/*
    Optional: deploy dbo.GetTotp (SQL CLR) to Authenticator on .\SQLEXPRESS (or your connection.json Server).

    Enables CLR. Trusts the signed assemblies with clr strict security ON.
    Does not set TRUSTWORTHY ON. Does not disable clr strict security.

    Expected DLL paths after:
      dotnet build D:\source\Authenticator\Authenticator.SqlClr\Authenticator.SqlClr.csproj -c Release

      D:\source\Authenticator\Authenticator.SqlClr\bin\Release\net48\Authenticator.SqlClr.dll
      D:\source\Authenticator\Authenticator.SqlClr\bin\Release\net48\Otp.NET.dll

    Otp.NET.dll is the net461 build copied next to Authenticator.SqlClr.dll.
    Both DLLs are strong-named. Each gets an asymmetric key in master and
    GRANT UNSAFE ASSEMBLY (HMAC). PERMISSION_SET = UNSAFE.

    If you regenerated Authenticator.SqlClr.snk, drop the Authenticator login/key
    in master before re-running:
      USE master;
      DROP LOGIN AuthenticatorSqlClrLogin;
      DROP ASYMMETRIC KEY AuthenticatorSqlClrKey;

    Do not run this file from the app. Review, then SSMS or:
      sqlcmd -S .\SQLEXPRESS -E -i D:\source\Authenticator\DBSchema\Create_GetTotp.sql
*/

USE master;
GO

EXEC sp_configure N'show advanced options', 1;
RECONFIGURE;
GO

EXEC sp_configure N'clr enabled', 1;
RECONFIGURE;
GO

-- clr strict security stays ON (SQL Server 2017+ default).
-- Do not: EXEC sp_configure N'clr strict security', 0;
-- Do not: ALTER DATABASE Authenticator SET TRUSTWORTHY ON;

IF NOT EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE name = N'OtpNetSqlClrKey')
BEGIN
    CREATE ASYMMETRIC KEY OtpNetSqlClrKey
    FROM EXECUTABLE FILE = N'D:\source\Authenticator\Authenticator.SqlClr\bin\Release\net48\Otp.NET.dll';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'OtpNetSqlClrLogin')
BEGIN
    CREATE LOGIN OtpNetSqlClrLogin FROM ASYMMETRIC KEY OtpNetSqlClrKey;
END
GO

GRANT UNSAFE ASSEMBLY TO OtpNetSqlClrLogin;
GO

IF NOT EXISTS (SELECT 1 FROM sys.asymmetric_keys WHERE name = N'AuthenticatorSqlClrKey')
BEGIN
    CREATE ASYMMETRIC KEY AuthenticatorSqlClrKey
    FROM EXECUTABLE FILE = N'D:\source\Authenticator\Authenticator.SqlClr\bin\Release\net48\Authenticator.SqlClr.dll';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'AuthenticatorSqlClrLogin')
BEGIN
    CREATE LOGIN AuthenticatorSqlClrLogin FROM ASYMMETRIC KEY AuthenticatorSqlClrKey;
END
GO

GRANT UNSAFE ASSEMBLY TO AuthenticatorSqlClrLogin;
GO

USE Authenticator;
GO

IF OBJECT_ID(N'dbo.GetTotp') IS NOT NULL
    DROP FUNCTION dbo.GetTotp;
GO

IF EXISTS (SELECT 1 FROM sys.assemblies WHERE name = N'Authenticator.SqlClr')
    DROP ASSEMBLY [Authenticator.SqlClr];
GO

IF EXISTS (SELECT 1 FROM sys.assemblies WHERE name = N'Otp.NET')
    DROP ASSEMBLY [Otp.NET];
GO

CREATE ASSEMBLY [Otp.NET]
FROM N'D:\source\Authenticator\Authenticator.SqlClr\bin\Release\net48\Otp.NET.dll'
WITH PERMISSION_SET = UNSAFE;
GO

CREATE ASSEMBLY [Authenticator.SqlClr]
FROM N'D:\source\Authenticator\Authenticator.SqlClr\bin\Release\net48\Authenticator.SqlClr.dll'
WITH PERMISSION_SET = UNSAFE;
GO

CREATE FUNCTION dbo.GetTotp(@AccountId INT)
RETURNS NVARCHAR(8)
AS EXTERNAL NAME [Authenticator.SqlClr].[Authenticator.SqlClr.TotpFunctions].[GetTotp];
GO
