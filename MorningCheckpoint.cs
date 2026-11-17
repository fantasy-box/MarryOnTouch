using StardewValley;

namespace MarryOnTouch;

internal sealed class MorningCheckpoint
{
    private string? slot;

    public bool IsReady => slot is not null;

    public void Prepare()
    {
        Clear();
        string currentSlot = SaveGame.FilterFileName(Game1.GetSaveGameName()) + "_" + Game1.uniqueIDForThisGame;
        string saveFile = Path.Combine(Program.GetSavesFolder(), currentSlot, currentSlot);

        if (!File.Exists(saveFile) && !File.Exists(saveFile + ".xml"))
        {
            if (File.Exists(saveFile + "_old") || File.Exists(Path.Combine(Path.GetDirectoryName(saveFile)!, "SaveGameInfo_old")))
                throw new IOException("A previous save exists, but the current morning save is missing.");
            if (Game1.timeOfDay != 600)
                throw new InvalidOperationException("A new checkpoint can only be created at 06:00.");

            // A new farm usually has no disk save until its first night.
            // Create its initial save using the game's regular serializer.
            byte gameMode = Game1.gameMode;
            string debugOutput = Game1.debugOutput;
            try
            {
                using IEnumerator<int> save = SaveGame.getSaveEnumerator();
                while (save.MoveNext() && save.Current < 100) { }
                if (!File.Exists(saveFile))
                    throw new IOException("Stardew Valley did not write the initial morning save.");
            }
            finally
            {
                Game1.gameMode = gameMode;
                Game1.debugOutput = debugOutput;
            }
        }

        slot = currentSlot;
    }

    public void Load() => SaveGame.Load(slot ?? throw new InvalidOperationException("No morning checkpoint."));

    public void Clear() => slot = null;
}
