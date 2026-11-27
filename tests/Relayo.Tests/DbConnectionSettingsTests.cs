using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Relayo.Internal.Configuration;
using Relayo.ServiceDefaults;

namespace Relayo.Tests;

public class DbConnectionSettingsTests
{
    private static RelayoConfig Config(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    private static readonly (string, string)[] Server = [("MSSQL_HOST", "db.local"), ("MSSQL_PORT", "1434"), ("MSSQL_DB", "relayo")];

    [Fact]
    public void Sql_login_is_the_default_and_requires_user_and_password()
    {
        var config = Config(Server);
        LoadDb(config);
        Assert.Equal(["MSSQL_USER is not set", "MSSQL_PASSWORD is not set"], config.Errors);

        config = Config([.. Server, ("MSSQL_USER", "relayo"), ("MSSQL_PASSWORD", "Passw0rd")]);
        var db = LoadDb(config);
        Assert.Empty(config.Errors);
        Assert.Equal(("db.local,1434", "relayo", "relayo", "Passw0rd"), (db.DataSource, db.InitialCatalog, db.UserID, db.Password));
        Assert.False(db.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, db.Encrypt);
    }

    [Fact]
    public void Windows_authentication_uses_the_process_account_and_ignores_user_and_password()
    {
        var config = Config([.. Server, ("MSSQL_AUTH", "Windows"), ("MSSQL_USER", "sa"), ("MSSQL_PASSWORD", "Passw0rd")]);
        var db = LoadDb(config);

        Assert.Empty(config.Errors);
        Assert.True(db.IntegratedSecurity);
        Assert.Equal(("", ""), (db.UserID, db.Password));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, db.Encrypt);
    }

    [Fact]
    public void Unknown_authentication_mode_is_reported()
    {
        var config = Config([.. Server, ("MSSQL_AUTH", "kerberos")]);
        LoadDb(config);
        Assert.Equal(["MSSQL_AUTH must be sql or windows, got 'kerberos'"], config.Errors);
    }

    private static SqlConnectionStringBuilder LoadDb(RelayoConfig config) =>
        new(InternalSettings.LoadDbConnectionString(config));
}
