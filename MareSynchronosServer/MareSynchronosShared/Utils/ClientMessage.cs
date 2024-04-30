using MareSynchronos.API.Data.Enum;

namespace MareSynchronosShared.Utils;
public record ClientMessage(MessageSeverity Severity, string Message, string UID);
