using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace MrWhoOidc.UnitTests;

[TestClass]
public static class AssemblyTestEnvironment
{
	[AssemblyInitialize]
	public static void Initialize(TestContext _)
	{
		Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
	}
}
