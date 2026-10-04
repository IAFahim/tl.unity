# Tl.Runtime.Unity 3.0.1-preview.1

Local NuGetForUnity runtime profile targeting .NET Standard 2.1, pinned to authoritative tl v3 source a464bfe. It shares TLB format 4 and SlotView ABI 3 with published Tl.CSharp 3.0.0. Published Tl.Runtime 3.0.0 remains immutable and net10.0-only.

The profile projects the canonical decoder, intern lifetime, Fold machinery, lane encoding and ColumnSet dispatch. Preparation is cold. Unity jobs receive prepared unmanaged views; the caller owns columns, jobs, clocks and world lifetime. .NET intrinsics and ref-struct bake context APIs are outside this profile.

This package is prepared locally for review, not published. See the repository's native v3 profile guide for installation and actual test receipts.
