using System.Reflection;

namespace ParadisERP.Tests.Architecture;

public class LayeringTests
{
    [Fact]
    public void Domain_has_no_project_dependencies()
    {
        var references = Assembly.Load("ParadisERP.Domain").GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is not null && reference.Name.StartsWith("ParadisERP.", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var references = Assembly.Load("ParadisERP.Application").GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is "ParadisERP.Infrastructure" or "ParadisERP.API");
    }
}
