namespace MareSynchronosStaticFilesServer.Utils;

public record UserRequest(Guid RequestId, string User, List<string> FileIds);
