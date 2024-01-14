namespace MareSynchronosServer.Authentication;

public record SecretKeyAuthReply(bool Success, string Uid, string PrimaryUid, string Alias, bool TempBan, bool Permaban);
