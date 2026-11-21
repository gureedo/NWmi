namespace NWmi.IntegrationTests;

public class EnvironmentTests
{
    [IntegrationFact]
    public void Target_host_is_configured() => Assert.False(string.IsNullOrEmpty(IntegrationSettings.Host));
}
