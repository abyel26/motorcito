using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

/// <summary>
/// Community signal catalogs are removable by design. These fail the build if
/// the protocol layer ever starts depending on one directly.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void The_protocol_layer_does_not_depend_on_any_signal_catalog()
    {
        var references = typeof(Elm327Session).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Motorcito.Profiles", StringComparison.Ordinal));
    }
}
