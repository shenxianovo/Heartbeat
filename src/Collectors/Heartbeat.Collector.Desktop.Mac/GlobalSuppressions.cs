using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Interoperability",
    "CA2101:Specify marshaling for P/Invoke string arguments",
    Justification = "These native macOS APIs consume UTF-8 C strings; each affected parameter is explicitly marshaled as LPUTF8Str.")]
