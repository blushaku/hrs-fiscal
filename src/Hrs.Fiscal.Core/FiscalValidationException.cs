namespace Hrs.Fiscal.Core;

public sealed class FiscalValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public FiscalValidationException(string error) : this(new[] { error }) { }

    public FiscalValidationException(IReadOnlyList<string> errors)
        : base("Fiscal coupon is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }
}
