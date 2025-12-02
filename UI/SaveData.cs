using System;
using System.Collections.Generic;

[Serializable]
public class SaveData
{
	public int HighestUnlockedLevel = 1;      // 1-based index; Level 1 unlocked by default
	public HashSet<int> CompletedLevels = new(); // optional, per-level completion
	public string LastScene = "res://MainMenu.tscn"; // optional
}
