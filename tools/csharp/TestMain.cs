// Entry point for running the Unity EditMode NUnit tests headlessly (tools/run_csharp_tests.mjs).
using NUnitLite;

public static class TestMain
{
    public static int Main(string[] args) => new AutoRun(typeof(TestMain).Assembly).Execute(args);
}
