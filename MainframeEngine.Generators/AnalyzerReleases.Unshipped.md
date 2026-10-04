; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MFG001 | MainframeEngine.Serialization | Error | [Export] member is not accessible
MFG002 | MainframeEngine.Serialization | Error | [Export] type is not serializable
MFG003 | MainframeEngine.Serialization | Error | [Signal] event is not accessible
MFG004 | MainframeEngine.Serialization | Error | Duplicate scene type name
MFG005 | MainframeEngine.Serialization | Error | Invalid serialized migration
MFG006 | MainframeEngine.Serialization | Error | Node or resource type is not accessible
MFG007 | MainframeEngine.Networking | Error | Invalid [Replicated] member
MFG008 | MainframeEngine.Networking | Error | Invalid [Rpc] method
MFG009 | MainframeEngine.Networking | Error | Too many [Replicated] members
