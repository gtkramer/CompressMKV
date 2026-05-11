namespace MkvHelper;

/// <summary>
/// Resource costs declared by one pipeline operation, used by
/// <see cref="ResourcePool"/> to gate scheduling.  All four counts are
/// non-negative; zero means "this operation doesn't touch that resource."
///
/// Construction is positional-only with named arguments at call sites so the
/// resource being requested stays unambiguous:
/// <code>
/// new ResourceRequest(Cpu: 4, Nvenc: 1)
/// </code>
/// </summary>
public readonly record struct ResourceRequest(int Cpu = 0, int Nvenc = 0, int Nvdec = 0, int Cuda = 0);
