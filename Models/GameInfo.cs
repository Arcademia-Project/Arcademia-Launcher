using System;
using System.Collections.Generic;

namespace ArcademiaGameLauncher.Models
{
    public class GameInfo
    {
        public int Id { get; set; }
        public string VersionNumber { get; set; } = null!;

        public string Name { get; set; } = null!;
        public string Description { get; set; } = null!;
        public string? ThumbnailUrl { get; set; }

        public ICollection<Author> Authors { get; set; } = [];
        public ICollection<Tag> Tags { get; set; } = [];

        public string FileUrl { get; set; } = null!;
        public string NameOfExecutable { get; set; } = null!;
        public string FolderName { get; set; } = null!;

        public DateTime? UploadedAt { get; set; }

        public bool Quarantined { get; set; }
    }
}
