using Motorcito.Data;

namespace Motorcito.Data.Tests;

/// <summary>
/// Community signal catalogs are removable by design. This fails the build if
/// storage ever starts depending on one directly.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Storage_does_not_depend_on_any_signal_catalog()
    {
        var references = typeof(LoggingService).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Motorcito.Profiles", StringComparison.Ordinal));
    }
}
