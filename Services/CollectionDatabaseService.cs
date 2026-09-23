using System.IO;
using ArcademiaGameLauncher.Models;
using Newtonsoft.Json;

namespace ArcademiaGameLauncher.Services
{
    public class CollectionDatabaseService
    {
        public static CollectionInfo[] LoadCollectionDatabase(string gameDirectoryPath)
        {
            string collectionDatabasePath = Path.Combine(
                gameDirectoryPath,
                "CollectionDatabase.json"
            );
            if (File.Exists(collectionDatabasePath))
            {
                string json = File.ReadAllText(collectionDatabasePath);
                return JsonConvert.DeserializeObject<CollectionInfo[]>(json) ?? [];
            }
            else
            {
                return [];
            }
        }
    }
}
