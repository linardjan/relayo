using Relayo.ServiceDefaults;

namespace Relayo.Tests;

public class DotEnvTests
{
    [Fact]
    public void Parses_values_comments_and_quotes()
    {
        var parsed = DotEnv.Parse(
        [
            "# comment",
            "",
            "PLAIN=value",
            "  SPACED = padded  ",
            "QUOTED=\"CN=relayo-sync,OU=Service Accounts,DC=example,DC=local\"",
            "SINGLE='single'",
            "EMPTY=",
            "BASE64=abc+/def==",
            "not a pair",
        ]).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal("value", parsed["PLAIN"]);
        Assert.Equal("padded", parsed["SPACED"]);
        Assert.Equal("CN=relayo-sync,OU=Service Accounts,DC=example,DC=local", parsed["QUOTED"]);
        Assert.Equal("single", parsed["SINGLE"]);
        Assert.Equal("", parsed["EMPTY"]);
        Assert.Equal("abc+/def==", parsed["BASE64"]);
        Assert.Equal(6, parsed.Count);
    }

    [Fact]
    public void Repository_env_example_is_parseable_and_has_no_secrets()
    {
        var example = DotEnv.Find(AppContext.BaseDirectory) is { } envPath
            ? Path.Combine(Path.GetDirectoryName(envPath)!, ".env.example")
            : FindUp(".env.example");
        Assert.True(File.Exists(example), ".env.example not found");

        var parsed = DotEnv.Parse(File.ReadAllLines(example)).ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var secret in new[] { "JWT_SIGNING_KEY", "SECRETS_ENCRYPTION_KEY", "INTERNAL_GRPC_KEY", "TEST_SITE_SECRET", "BOOTSTRAP_ADMIN_PASSWORD" })
            Assert.Equal("", parsed[secret]);
    }

    private static string FindUp(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, fileName)))
                return Path.Combine(dir.FullName, fileName);
        return fileName;
    }
}
