namespace MareSynchronos.API
{
    public interface ITransferFileDto
    {
        string Hash { get; set; }
        bool IsForbidden { get; set; }
        string ForbiddenBy { get; set; }
    }
}