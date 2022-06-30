using System;

namespace MareSynchronos.API
{
    public record FileReplacementDto
    {
        public string[] GamePaths { get; set; } = Array.Empty<string>();
        public string Hash { get; set; }
    }
}