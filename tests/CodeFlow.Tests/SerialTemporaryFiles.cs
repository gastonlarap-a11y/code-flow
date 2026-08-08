using Xunit;

namespace CodeFlow.Tests;

/// <summary>
/// Tests that count what is sitting in the system temporary directory, and so cannot run beside
/// anything that puts something there.
/// </summary>
/// <remarks>
/// The scratch directories Azure's diff assembly creates are named per call, but a test that asserts
/// none survived can only do so by counting — and a count is wrong the moment another test is
/// mid-render. Rather than weaken the assertion into one that would no longer catch a leak, the few
/// tests that make it run alone.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTemporaryFiles
{
    public const string Name = "serial-temporary-files";
}
