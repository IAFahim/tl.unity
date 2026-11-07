
namespace Tl.Gen.Tlb;

public class BakeDiagnosticException : Exception
{
    public BakeDiagnosticException(string message) : base(message) { }
    public BakeDiagnosticException(string message, int line, int column)
        : base($"[{line}:{column}] {message}") { }
}
