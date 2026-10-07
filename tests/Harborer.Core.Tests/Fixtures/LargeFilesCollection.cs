namespace Harborer.Core.Tests.Fixtures;

/// <summary>Large-file and timing tests run alone so that other tests do not compete for the CPU.</summary>
[CollectionDefinition("Large files", DisableParallelization = true)]
public sealed class LargeFilesCollection;
