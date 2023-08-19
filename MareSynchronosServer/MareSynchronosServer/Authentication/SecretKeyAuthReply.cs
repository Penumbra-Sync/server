namespace MareSynchronosServer.Authentication;

public record SecretKeyAuthReply(bool Success, string Uid, string PrimaryUid, bool TempBan, bool Permaban);
