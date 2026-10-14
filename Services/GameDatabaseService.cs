using System.IO;
using ArcademiaGameLauncher.Models;
using Newtonsoft.Json.Linq;

namespace ArcademiaGameLauncher.Services
{
    public class GameDatabaseService
    {
        public static JObject[] LoadGameDatabase(string gameDirectoryPath)
        {
            // Load the game database from the GameDatabase.json file
            string gameDatabasePath = Path.Combine(gameDirectoryPath, "GameDatabase.json");
            if (File.Exists(gameDatabasePath))
            {
                JArray gameInfoArray = JArray.Parse(File.ReadAllText(gameDatabasePath));

                JObject[] gameInfoList = new JObject[gameInfoArray.Count];
                for (int i = 0; i < gameInfoArray.Count; i++)
                    gameInfoList[i] = gameInfoArray[i].ToObject<JObject>();

                return gameInfoList;
            }
            else
            {
                return [];
            }
        }

        public static GameState[] ValidateGameExecutables(
            JObject[] gameInfoList,
            string gameDirectoryPath
        )
        {
            GameState[] gameTitleStates = new GameState[gameInfoList.Length];

            for (int i = 0; i < gameInfoList.Length; i++)
            {
                // If the game's exe exists set the title state to ready
                string folderName = gameInfoList[i]["FolderName"]?.ToString();
                string executableName = gameInfoList[i]["NameOfExecutable"]?.ToString();

                if (IsInstalled(gameDirectoryPath, folderName, executableName))
                {
                    gameTitleStates[i] = GameState.ready;
                }
                else
                {
                    gameTitleStates[i] = GameState.fetchingInfo;
                }
            }

            return gameTitleStates;
        }

        public static bool IsInstalled(
            string gameDirectoryPath,
            string? folderName,
            string? executableName
        ) =>
            !string.IsNullOrEmpty(folderName)
            && !string.IsNullOrEmpty(executableName)
            && File.Exists(Path.Combine(gameDirectoryPath, folderName, executableName))
            && File.Exists(Path.Combine(gameDirectoryPath, folderName, InstalledMarkerName));

        public const string InstalledMarkerName = ".arcademia-version";
    }
}
