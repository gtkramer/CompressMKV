namespace MkvHelper;

/// <summary>
/// Pure binary search for the highest integer CQ in a closed range that
/// satisfies a caller-provided pass predicate.  Pulled out of
/// <see cref="VmafTuner"/> so the search arithmetic can be unit-tested
/// independently of the heavy I/O it drives in production (NVENC encode
/// + libvmaf_cuda probe per step).
///
/// Invariants:
///   - The probe is invoked for each candidate at most once.
///   - Termination is guaranteed: <c>hi - lo</c> strictly decreases on
///     every iteration.
///   - Convergence: <c>O(log2(MaxCq - MinCq + 1))</c> probes worst case.
///
/// Monotonicity assumption: if <c>probe(x)</c> passes, every CQ ≤ x also
/// would.  This holds for VMAF-vs-CQ on real content modulo small noise
/// at adjacent CQs (NVENC's spatial/temporal AQ can flip a probe at the
/// margin).  Off-by-one near the gate boundary is acceptable for our
/// transparent-quality target.
/// </summary>
public static class CqBinarySearch
{
    /// <summary>
    /// Returns the highest CQ in <c>[minCq, maxCq]</c> for which
    /// <paramref name="probe"/> returned true, or <c>null</c> if no probe
    /// passed.  The +1 bias in the mid formula starts the search at the
    /// upper midpoint of the range — for the default <c>[25, 55]</c>, the
    /// first probe is CQ=40.
    ///
    /// <paramref name="onStep"/> is invoked before each probe with the
    /// current <c>(lo, hi, mid)</c> so the caller can render the search
    /// state inline with its per-probe log.  Optional; null disables the
    /// callback.
    /// </summary>
    public static async Task<int?> FindHighestPassingAsync(
        int minCq, int maxCq,
        Func<int, Task<bool>> probe,
        CancellationToken ct = default,
        Action<int, int, int>? onStep = null)
    {
        if (minCq > maxCq)
            throw new ArgumentException($"minCq ({minCq}) must be <= maxCq ({maxCq}).");

        int lo = minCq;
        int hi = maxCq;
        int? best = null;

        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = (lo + hi + 1) / 2;
            onStep?.Invoke(lo, hi, mid);

            if (await probe(mid))
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }
}
