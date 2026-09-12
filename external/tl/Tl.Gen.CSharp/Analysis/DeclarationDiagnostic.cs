namespace Tl.Gen.CSharp.Analysis;

public sealed record DeclarationDiagnostic(
    string File,
    int Line,
    int Column,
    string Code,
    string Message)
{
    internal int SpanStart { get; init; } = -1;
    internal int SpanLength { get; init; }
    internal int EndLine { get; init; }
    internal int EndColumn { get; init; }

    public override string ToString() => $"{File}({Line},{Column}): error {Code}: {Message}";
}
