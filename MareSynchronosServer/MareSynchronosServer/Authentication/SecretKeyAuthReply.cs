namespace MareSynchronosServer.Authentication;

public record SecretKeyAuthReply(bool Success, string Uid, bool TempBan, bool Permaban);
