using System.Reflection;

namespace AlgeriaPos.Tests.Architecture;

public class LayeringTests
{
    [Fact]
    public void Domain_has_no_project_dependencies()
    {
        var references = Assembly.Load("AlgeriaPos.Domain").GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is not null && reference.Name.StartsWith("AlgeriaPos.", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var references = Assembly.Load("AlgeriaPos.Application").GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is "AlgeriaPos.Infrastructure" or "AlgeriaPos.API");
    }
}
