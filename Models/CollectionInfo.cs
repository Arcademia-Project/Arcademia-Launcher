using System.Collections.Generic;

namespace ArcademiaGameLauncher.Models
{
    public class CollectionInfo
    {
        public int CollectionId { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public string? ImageURL { get; set; }
        public int CustomOrder { get; set; }
        public List<int> GameIds { get; set; } = [];
    }
}
