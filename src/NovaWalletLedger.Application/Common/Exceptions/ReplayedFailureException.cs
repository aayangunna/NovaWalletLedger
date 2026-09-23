namespace NovaWalletLedger.Application.Common.Exceptions;

/// <summary>
/// Thrown when an Idempotency-Key is replayed and the original request had
/// failed. Carries the exact status code and Problem Details JSON body that
/// were persisted for that key, so the replay returns the same failure
/// verbatim instead of re-deriving or re-attempting it.
/// </summary>
public sealed class ReplayedFailureException : Exception
{
    public ReplayedFailureException(int statusCode, string problemDetailsJson)
        : base($"Replayed a previously-failed request (status {statusCode}).")
    {
        StatusCode = statusCode;
        ProblemDetailsJson = problemDetailsJson;
    }

    public int StatusCode { get; }
    public string ProblemDetailsJson { get; }
}
