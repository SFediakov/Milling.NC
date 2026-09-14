// PLACEHOLDER - implemented by T-046 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the tests.
// Namespace: Miller.Tests.Application
// Tests for: src/Miller.Application/Services/PipelineService.cs
// Required cases: T-046: fixture import report; T-048: box completes with zero gouges, roughing and
//     finishing present, progress non-decreasing ending at 1, cancellation throws, invalid project
//     throws ValidationException with field; T-049: fixture at 0.2 within 60 s, zero gouges, rest
//     area above zero, bounds inside stock plus safe height
// Rules: deterministic, no network, temp directories only, every strategy test calls GougeChecker.Verify.
