using System;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using Microsoft.SqlServer.Server;
using OtpNet;

namespace Authenticator.SqlClr
{
    public static class TotpFunctions
    {
        [SqlFunction(
            Name = "GetTotp",
            DataAccess = DataAccessKind.Read,
            SystemDataAccess = SystemDataAccessKind.None,
            IsDeterministic = false,
            IsPrecise = true)]
        public static SqlString GetTotp(SqlInt32 accountId)
        {
            if (accountId.IsNull)
                return SqlString.Null;

            try
            {
                string secretKey;
                string algorithm;
                int digits;
                int period;

                using (var connection = new SqlConnection("context connection=true"))
                {
                    connection.Open();
                    using (var command = new SqlCommand(
                        "SELECT SecretKey, Algorithm, Digits, Period FROM dbo.Accounts WHERE Id = @AccountId AND IsDeleted = 0",
                        connection))
                    {
                        command.Parameters.Add(new SqlParameter("@AccountId", SqlDbType.Int)
                        {
                            Value = accountId.Value
                        });

                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read())
                                return SqlString.Null;

                            secretKey = reader.IsDBNull(0) ? null : reader.GetString(0);
                            algorithm = reader.IsDBNull(1) ? "SHA1" : reader.GetString(1);
                            digits = reader.IsDBNull(2) ? 6 : reader.GetInt32(2);
                            period = reader.IsDBNull(3) ? 30 : reader.GetInt32(3);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(secretKey))
                    return SqlString.Null;

                var secret = NormalizeSecret(secretKey);
                var totp = new Totp(secret, step: period, mode: ParseMode(algorithm), totpSize: digits);
                var code = totp.ComputeTotp();
                if (string.IsNullOrEmpty(code))
                    return SqlString.Null;

                return new SqlString(code);
            }
            catch
            {
                // Missing account, bad secret, or generation failure. Do not return or log the secret.
                return SqlString.Null;
            }
        }

        private static byte[] NormalizeSecret(string secretKey)
        {
            var normalized = secretKey.Replace(" ", string.Empty)
                                      .Replace("-", string.Empty)
                                      .ToUpperInvariant();
            return Base32Encoding.ToBytes(normalized);
        }

        private static OtpHashMode ParseMode(string algorithm)
        {
            if (algorithm == null)
                return OtpHashMode.Sha1;

            switch (algorithm.ToUpperInvariant())
            {
                case "SHA256":
                    return OtpHashMode.Sha256;
                case "SHA512":
                    return OtpHashMode.Sha512;
                default:
                    return OtpHashMode.Sha1;
            }
        }
    }
}
