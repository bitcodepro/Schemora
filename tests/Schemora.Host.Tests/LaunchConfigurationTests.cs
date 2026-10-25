using System.Net.Sockets;
using Schemora.Host;
using Xunit;

namespace Schemora.Host.Tests;

public sealed class LaunchConfigurationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SchemoraLaunchConfig-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(directory, "schemora.json");

    public LaunchConfigurationTests() => Directory.CreateDirectory(directory);

    private void Write(string json) => File.WriteAllText(ConfigPath, json);

    [Fact]
    public void MissingDefaultUsesAutomaticPortWithoutCreatingConfig()
    {
        var settings = LaunchConfiguration.Load([], directory);
        Assert.Equal(0, settings.Port);
        Assert.Equal(ConfigPath, settings.ConfigPath);
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void ValidJsonIsReadWithoutMutatingTheSource(int port)
    {
        var json = "{\n  \"port\": " + port + "\n}\n";
        Write(json);
        var timestamp = File.GetLastWriteTimeUtc(ConfigPath);
        Assert.Equal(port, LaunchConfiguration.Load([], directory).Port);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(ConfigPath));
    }

    [Fact]
    public void CommandLineOverridesJsonAndExplicitConfigResolvesIndependentlyOfApplicationDirectory()
    {
        Write("{\"port\":17}");
        Assert.Equal(0, LaunchConfiguration.Load(["--config", ConfigPath, "--port", "0"], Path.Combine(directory, "other")).Port);
        Assert.Equal(443, LaunchConfiguration.Load(["--port=443", "--config=" + ConfigPath], directory).Port);
        Assert.Equal("{\"port\":17}", File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("not-json", "LAUNCH_CONFIG_INVALID_JSON")]
    [InlineData("null", "LAUNCH_CONFIG_INVALID")]
    [InlineData("[]", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":\"123\"}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":true}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":1.5}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":-1}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":65536}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":1,\"port\":2}", "LAUNCH_CONFIG_INVALID")]
    [InlineData("{\"port\":0,\"host\":\"0.0.0.0\"}", "LAUNCH_CONFIG_INVALID")]
    public void InvalidJsonShapeTypeRangeAndDuplicatesFailWithTheConfigPath(string json, string code)
    {
        Write(json);
        var error = Assert.Throws<AppException>(() => LaunchConfiguration.Load([], directory));
        Assert.Equal(code, error.Code);
        Assert.Contains(ConfigPath, error.Message);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("+1")]
    public void InvalidCommandLinePortFailsInsteadOfUsingAutomatic(string port)
    {
        Assert.Equal("LAUNCH_PORT_INVALID", Assert.Throws<AppException>(() => LaunchConfiguration.Load(["--port", port], directory)).Code);
    }

    [Fact]
    public void MissingDuplicateAndUnreadableOptionsFailVisibly()
    {
        Assert.Equal("LAUNCH_ARGUMENT_INVALID", Assert.Throws<AppException>(() => LaunchConfiguration.Load(["--port"], directory)).Code);
        Assert.Equal(
            "LAUNCH_ARGUMENT_INVALID",
            Assert.Throws<AppException>(() => LaunchConfiguration.Load(["--port", "1", "--port", "2"], directory)).Code
        );
        Assert.Equal(
            "LAUNCH_CONFIG_NOT_FOUND",
            Assert.Throws<AppException>(() => LaunchConfiguration.Load(["--config", ConfigPath], directory)).Code
        );
        Write(new string(' ', LaunchConfiguration.MaximumFileBytes + 1));
        Assert.Equal("LAUNCH_CONFIG_TOO_LARGE", Assert.Throws<AppException>(() => LaunchConfiguration.Load([], directory)).Code);
    }

    [Fact]
    public void JsonIsStillValidatedWhenACommandLinePortOverridesItsValue()
    {
        Write("not-json");
        Assert.Equal(
            "LAUNCH_CONFIG_INVALID_JSON",
            Assert.Throws<AppException>(() => LaunchConfiguration.Load(["--port", "0"], directory)).Code
        );
    }

    [Fact]
    public void NestedAddressInUseDetectionDoesNotMislabelOtherFailures()
    {
        Assert.True(LaunchConfiguration.IsAddressInUse(new IOException("bind", new SocketException((int)SocketError.AddressAlreadyInUse))));
        Assert.False(LaunchConfiguration.IsAddressInUse(new IOException("unrelated", new SocketException((int)SocketError.AccessDenied))));
        var message = new LaunchConfiguration(1234, ConfigPath).AddressInUseMessage();
        Assert.Contains("1234", message);
        Assert.Contains(ConfigPath, message);
        Assert.Contains("127.0.0.1", message);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
