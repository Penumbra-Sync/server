namespace MareSynchronosShared.RequirementHandlers;

public enum UserRequirements
{
    Identified = 0b00000001,
    Moderator = 0b00000010,
    Administrator = 0b00000100,
}
